# Phase 4 Rehberi: Telemetri işleme, canlı yayın ve geçmiş

Phase 3'te araçtan telemetri almayı öğrendik. Phase 4'te o veriyle **ne yapacağımızı** çözdük: operatörün ekranına
canlı göndermek, geçmişi saklamak ve "araç bağlandı / koptu" gibi olayları sistemin geri kalanına duyurmak.
Teknik referans: [docs/telemetry.md](../telemetry.md).

---

## 1. Problem: Saniyede 50 mesaj

Simülatör (ve gerçek PX4) her saniye konum, attitude, HUD, GPS ve batarya mesajları gönderiyor: araç başına
saniyede ~50 mesaj. 10 araç ve 5 operatör ekranı düşün:

| Saf (naif) yaklaşım | Sonuç |
|---|---|
| Her mesajı veritabanına yaz | 10 araç × 50 = saniyede 500 INSERT, ayda ~1.3 milyar satır |
| Her mesajı her ekrana gönder | 500 × 5 = saniyede 2500 push; insan gözü zaten fark etmez |
| Her şeyi alma döngüsünde yap | Veritabanı yavaşlarsa MAVLink okuma durur → bağlantı "koptu" sanılır |

Son satır en tehlikelisi: **yavaş bir tüketici, uçuş kritik yolu durduramaz.** Phase 4'ün ana fikri bu.

## 2. Çözüm: Her tüketiciye kendi hızı

```
araç mesajı (50 Hz)
  └─► TelemetryPipeline  (sadece bellek, mikro saniyeler)
        ├─► son durum (her mesajda güncellenir)
        │     ├─► REST: GET /telemetry  (istendiğinde)
        │     └─► Broadcaster: her 200 ms → sadece DEĞİŞEN araçları SignalR ile gönder   (5 Hz)
        └─► geçmiş tamponu: araç başına saniyede 1 örnek → kuyruk                        (1 Hz)
              └─► Writer: 500 örnek veya 2 sn'de bir → PostgreSQL'e TEK seferde yaz
```

### Throttling (kısma) örneği

Operatör ekranı için 5 Hz yeterli. `TelemetryBroadcaster` her 200 ms'de bir uyanıyor ve son durumu gönderiyor. Arada
10 mesaj gelmiş olsa bile **sadece en sonuncusu** gidiyor. Unit test tam bunu kontrol ediyor: 10 güncelleme →
1 push, o da en son konumla. Entegrasyon testinde 2 saniyede 4–13 arası push ölçülüyor; throttling olmasaydı ~100
olurdu.

Bir de: **değişmeyen araç gönderilmez.** Yerde duran, telemetri göndermeyen araç için her 200 ms'de aynı veriyi
yollamak bant genişliği israfı.

### Batching (toplu yazma) örneği

500 satırı tek tek yazmak = veritabanına 500 gidiş-dönüş. Toplu yazmak = 1 gidiş-dönüş. `TelemetryHistoryWriter`
iki koşuldan hangisi önce gelirse yazıyor: **500 örnek birikti** veya **2 saniye geçti**. İkinci koşul neden var?
Tek araç varsa 500 örnek 8 dakikada birikir; 8 dakika veri kaybı riski kabul edilemez.

### Sampling (örnekleme)

Geçmiş için saniyede 1 örnek: uçuş rotasını çizmeye ve grafiklere yeter. Araç başına ayda ~2.6 milyon satır.
Ham mesajların tamamı ~130 milyon olurdu.

## 3. Bounded queue + DropOldest: Veritabanı çökerse ne olur?

Örnekler `Channel` (thread-safe kuyruk) içinde bekliyor. Kuyruğun **kapasitesi sınırlı** (10.000) ve dolarsa
**en eski örneği atıyor** (`BoundedChannelFullMode.DropOldest`).

Senaryo: PostgreSQL 5 dakika çöktü.
- Canlı telemetri: **etkilenmez**, bellekte.
- Geçmiş: kuyrukta birikir; dolunca en eski örnekler atılır ve **sayılır** (`DroppedSamples`, loglanıyor).
- PostgreSQL geri gelince: writer bekleyen grubu tekrar dener, kuyruğu boşaltır.

**Neden sınırsız kuyruk değil?** Saatlerce süren bir kesintide bellek dolar ve API çöker. O zaman canlı telemetri
de gider. Savunma sanayii bakış açısı: eski geçmişi kaybetmek kabul edilebilir, canlı görüntüyü kaybetmek edilemez.
**Neden DropNewest değil?** Kesinti bittiğinde en güncel veri daha değerlidir.

## 4. SignalR: Sunucudan istemciye anlık veri

REST'te istemci sorar, sunucu cevaplar. Canlı telemetride istemcinin sürekli "yeni bir şey var mı?" diye sorması
(polling) israf. **SignalR** kalıcı bir bağlantı (genelde WebSocket) açar ve sunucu istediği an veri gönderir.

İki hub:

| Hub | Ne için | Nasıl |
|---|---|---|
| `/hubs/telemetry` | Bir aracın canlı telemetrisi | İstemci `SubscribeVehicle(id)` çağırır, o aracın **grubuna** katılır |
| `/hubs/vehicles` | Filonun bağlantı durumları | Herkes alır |

**Grup** neden önemli? 10 araçlık filoda tek bir aracı izleyen ekran, diğer 9 aracın verisini almamalı. Sunucu
`Clients.Group("vehicle:abc")` diyerek sadece ilgilenenlere gönderiyor.

**Abone olunca hemen son durumu gönder:** Yoksa ekran bir sonraki değişikliğe kadar (veya araç yerdeyse hiç) boş kalır.

### Mimari detay: Neden `ILiveUpdatePublisher` arayüzü?

SignalR bir ASP.NET Core özelliği. Telemetri modülü (Gcs.Telemetry) ASP.NET Core'u bilmemeli. Architecture testi
bunu zaten yasaklıyor. Çözüm yine port + adapter:

```
Gcs.Telemetry  ──► ILiveUpdatePublisher (Application'da tanımlı arayüz)
                          ▲
Gcs.Api        ── SignalRLiveUpdatePublisher (IHubContext kullanır)
```

Yarın SignalR yerine gRPC streaming veya MQTT gelse sadece Api'deki adapter değişir.

Mesaj isimleri (`TelemetryUpdated`, `SubscribeVehicle`) `Gcs.Contracts.Realtime`'da sabit olarak duruyor. Phase 5'te
Avalonia istemcisi aynı sabitleri kullanacak, yazım hatası riski yok.

## 5. Bağlantı olayları: Outbox ile RabbitMQ'ya

Phase 2'de aracın kaydedilmesi `VehicleRegistered` olayı üretiyordu. Şimdi bağlantı da olay üretiyor:

| Geçiş | Olay |
|---|---|
| → Connected | `VehicleConnected` |
| Connected → Reconnecting | `VehicleLinkLost` |
| → Faulted | `VehicleLinkFaulted` |
| → Disconnected | `VehicleDisconnected` |
| Disconnected → Connecting | (olay yok, rutin) |

Akış:

```
MavlinkConnection (durum değişti)
  └─► IVehicleLinkEventSink.StateChanged()   ← sadece kuyruğa koyar, ANINDA döner
        └─► VehicleLinkEventDispatcher (arka plan)
              ├─► SignalR /hubs/vehicles     (ekran anında güncellenir)
              └─► outbox tablosu             (→ OutboxDispatcher → RabbitMQ "vehicle.connected" ...)
```

**Neden araya kuyruk?** `StateChanged` MAVLink bağlantısının kendi döngüsünden çağrılıyor. Orada veritabanına yazmak
için beklersek, veritabanı yavaşken bağlantı yöneticisi de yavaşlar. Kuyruğa koy, dön; gerisini başkası halletsin.

Entegrasyon testi sırayı da doğruluyor: RabbitMQ'ya `VehicleRegistered → VehicleConnected → VehicleDisconnected`
sırasıyla geliyor. Aggregate olayları ve bağlantı olayları **aynı outbox'tan** geçtiği için sıra korunuyor.

## 6. Redis kararı: Neden hâlâ yok?

Senin metnin "Redis if justified" diyordu. Ölçtüm ve düşündüm ([ADR-011](../adr/ADR-011-redis-evaluation-phase-4.md)):

- **Tek API sunucusu var.** Bellekten okumak nanosaniyeler, Redis'e gitmek yüzlerce mikrosaniye + serileştirme.
- **Yeniden başlatmadan sonra eski canlı veri zaten anlamsız.** Bağlantı yeniden kurulunca ~1 saniyede yeni veri geliyor.
- **Kalıcı geçmiş zaten PostgreSQL'de.**
- **Redis eklemek yeni bir arıza noktası.** Redis çökerse bütün operatör ekranları kararır. Bellekte bu risk yok.

Yani bugün Redis'in tek etkisi sistemi **daha yavaş ve daha kırılgan** yapmak olurdu. Ama ne zaman doğru araç
olacağını da yazdım:

1. **Birden fazla API sunucusu** olursa: SignalR backplane (bir sunucuya bağlı istemciye diğer sunucudan gelen
   veriyi iletmek) ve ortak son durum için.
2. **MAVLink bağlantıları ayrı worker process'lere** taşınırsa: worker'lardan API'ye akış için.

"Teknolojiyi CV için değil ihtiyaç için kullan" kuralının pratikteki hali bu: Redis'i reddetmedik, ne zaman
gerekeceğini ölçülebilir koşullarla tanımladık.

## 7. Bu phase'de yakalanan iki hata

1. **İki kez dispose:** Yeni bir test, bağlantıyı elle kapattıktan sonra test sınıfının da kapatmasıyla
   `ObjectDisposedException` aldı. Gerçek hayatta da olabilir: operatör "bağlantıyı kes"e basarken uygulama da
   kapanıyor olabilir. `DisposeAsync`'i **idempotent** yaptım (`Interlocked.Exchange` ile tek seferlik bayrak).
2. **Test beklenenden fazlasını gördü:** RabbitMQ testi sadece bağlantı olaylarını bekliyordu ama `VehicleRegistered`
   da geldi. Sistem doğruydu, test darmış. Testi kuyruğu araç kaydından **önce** bağlayacak şekilde düzelttim ve tam
   sırayı (3 olay) doğrulayan daha güçlü bir teste çevirdim.

## 8. Testler

| Proje | Sayı | Phase 4'te eklenenler |
|---|---|---|
| Unit | 177 | örnekleme hızı, araç bazlı örnekleme, dolu kuyrukta en eskiyi atma ve sayma, broadcaster throttling, değişmeyen aracı göndermeme, bir istemcinin hatasının diğerlerini etkilememesi, geçiş → olay eşlemesi, her durum değişikliğinin raporlanması |
| Architecture | 11 | Telemetry ASP.NET Core'u ve Persistence'ı bilmiyor (port'lar sayesinde) |
| Integration | 33 | gerçek SignalR ile canlı telemetri (5 Hz kısıtı ölçülüyor), filo bağlantı durumu push'u, PostgreSQL'de geçmiş, ters zaman aralığına 400, RabbitMQ'da sıralı olaylar |

## 9. Kendin dene

```powershell
docker compose up -d --build --wait
# (Phase 3 rehberindeki gibi SIM-01'i kaydet ve bağla)

# Son 10 dakikanın geçmişi:
Invoke-RestMethod "http://localhost:8080/api/v1/vehicles/<id>/telemetry/history?limit=5" | ConvertTo-Json -Depth 5

# Veritabanında kaç örnek var?
docker compose exec postgres psql -U gcs -d gcs -c "select count(*), min(recorded_at), max(recorded_at) from gcs.telemetry_samples;"

# RabbitMQ yönetim ekranında (http://localhost:15672) bir kuyruk oluştur, gcs.events'e "vehicle.*" ile bağla,
# sonra simülatörü durdur/başlat: VehicleLinkLost, VehicleLinkFaulted, VehicleConnected mesajlarını gör.
docker compose stop gcs-simulator
```

SignalR'ı görmek için küçük bir C# betiği (`dotnet run hub.cs`):

```csharp
#:package Microsoft.AspNetCore.SignalR.Client@10.0.12
using Microsoft.AspNetCore.SignalR.Client;

var hub = new HubConnectionBuilder().WithUrl("http://localhost:8080/hubs/vehicles").Build();
hub.On<object>("LinkStatusChanged", s => Console.WriteLine(s));
await hub.StartAsync();
Console.WriteLine("Dinleniyor... simülatörü durdurup başlat.");
await Task.Delay(Timeout.Infinite);
```
