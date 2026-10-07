# Phase 7 Rehberi: Komut sistemi (ARM, TAKEOFF, LAND, RTL...)

Phase 6'da araca bir **plan** yüklüyorduk. Araç o planı kendi başına uçuyordu. Phase 7'de operatör araca **şimdi** ne
yapacağını söylüyor: "motorları çalıştır", "30 metreye kalk", "olduğun yere in", "eve dön".

![Kontrol paneli](../images/gcs-desktop-phase7-control.png)

Bu görüntü de gerçek. Docker'daki SIM-01'e komutlar gönderildi: havadayken DISARM (araç reddetti), RTL (eve döndü ve
indi), ARM, TAKEOFF 30 m ve sonunda LAND. Sağ alttaki "Recent commands" listesi veritabanındaki denetim kaydından
(audit log) geliyor.

Komutun görev planından farkı, **geri alınamayan fiziksel bir eylem** olması. Yanlış bir mission upload'unu tekrar
yükleyerek düzeltebilirsin. Havadaki bir araca gönderilen yanlış DISARM ise aracı düşürür. Bu yüzden bu fazın büyük
kısmı "komutu nasıl gönderirim?" sorusuyla değil, "**ters giderse ne olur?**" sorusuyla ilgili.

---

## 1. Altı komut ve MAVLink karşılıkları

| Komut | MAVLink | Ne yapar |
|---|---|---|
| ARM | `MAV_CMD_COMPONENT_ARM_DISARM`, param1 = 1 | Motorları çalıştırır |
| DISARM | aynı komut, param1 = 0 | Motorları durdurur |
| TAKEOFF | `MAV_CMD_NAV_TAKEOFF`, param7 = irtifa | Dikey olarak kalkar |
| LAND | `MAV_CMD_NAV_LAND` | Olduğu yere iner |
| RTL | `MAV_CMD_NAV_RETURN_TO_LAUNCH` | Eve döner ve iner |
| SET_MODE | `MAV_CMD_DO_SET_MODE` | Uçuş modunu değiştirir (POSCTL, AUTO.LOITER...) |

Hepsi aynı mesajla gider: **COMMAND_LONG**. Bu mesajda komut numarası ve 7 tane float parametre vardır. Araç da
**COMMAND_ACK** ile cevap verir: `ACCEPTED`, `DENIED`, `FAILED`, `IN_PROGRESS`...

```
GCS ──COMMAND_LONG(ARM)──►  araç
GCS ◄──COMMAND_ACK(ACCEPTED)──
```

### Autopilot farkı: irtifa neye göre?

İlginç bir tuzak var. TAKEOFF'un param7'si:

* **PX4**'te deniz seviyesinden (AMSL) irtifadır.
* **ArduPilot**'ta evin üstündeki irtifadır.

Operatör her zaman "evin 30 m üstüne" düşünür. Ankara deniz seviyesinden yaklaşık 938 m yüksekte. PX4'e "30"
gönderirsek araç deniz seviyesinden 30 m'ye, yani **yerin 908 m altına** kalkmaya çalışır (reddeder ya da saçmalar).
Bu yüzden `CommandMapper` PX4 için ev irtifasını ekliyor:

```
ev irtifası = GLOBAL_POSITION_INT.alt (deniz seviyesi) − relative_alt (evin üstü)
             = 968 m − 30 m = 938 m
param7      = 938 + 30 = 968   (PX4)
param7      = 30               (ArduPilot)
```

Henüz konum telemetrisi gelmediyse ev irtifası bilinmiyordur. O durumda **tahmin etmiyoruz**, komutu
`command.unsupported` ile reddediyoruz. Savunma yazılımında "bilmiyorum" demek, yanlış bir tahminden iyidir.

## 2. Zaman aşımı ve yeniden deneme (timeout + retry)

Telsiz bağlantısında paketler kaybolur. Peki COMMAND_LONG kaybolursa ne olur? ACK hiç gelmez. Sonsuza kadar beklemek
olmaz. `CommandExchange` şöyle çalışıyor:

```
deneme 1: COMMAND_LONG(confirmation=0) ──►  (kayboldu)
          ... 1.5 s bekle, ACK yok ...
deneme 2: COMMAND_LONG(confirmation=1) ──►
          ◄── COMMAND_ACK(ACCEPTED)         → "Accepted, 2 deneme"
```

Dikkat edilmesi gereken üç kural var:

1. **`confirmation` alanı her denemede bir artar.** Araç böylece "bu yeni bir komut değil, öncekinin tekrarı" diye
   anlar.
2. **Ret kesindir, tekrar denenmez.** Araç `DENIED` dediyse cevap vermiş demektir. Tekrar sormak fikrini
   değiştirmez, sadece telsizi meşgul eder.
3. **`IN_PROGRESS` gelirse yeniden göndermeyiz, sadece bekleriz.** Araç işi almıştır ve üzerinde çalışıyordur.
   Tekrar göndermek işi baştan başlatabilir.

Bütün denemeler biterse (varsayılan 4 deneme × 1.5 s = 6 s) sonuç **TimedOut** olur ve API `504` döner.

### Timeout ≠ başarısız

Bu fazın en önemli derslerinden biri bu. Zaman aşımı "komut başarısız oldu" demek **değildir**, "**ne olduğunu
bilmiyoruz**" demektir. Belki komut hiç ulaşmadı. Belki ulaştı, araç uyguladı, ama ACK dönerken kayboldu.

Örnek: LAND gönderdin, 504 aldın. Araç belki şu an iniyor. Hemen bir TAKEOFF göndermek tehlikeli olabilir. Bu yüzden:

* Hata mesajı "araç durumunu kontrol et" diyor.
* Audit kaydı `Rejected` değil, `TimedOut` yazıyor.
* Operatör telemetriye (mod, irtifa, armed) bakıp karar veriyor.

## 3. Aynı komut iki kere: neden reddediyoruz?

Operatör ARM butonuna heyecanla iki kere bastı. Ne olmalı?

COMMAND_ACK'in içinde sadece komut numarası var, istek kimliği (request id) yok. İki ARM aynı anda yoldaysa, gelen
ACK hangisine ait, bilemeyiz. Çözüm: **aynı MAV_CMD'den yolda en fazla bir tane olur.** İkincisi hemen
`409 command.in_flight` alır:

```csharp
// MavlinkConnection.SendCommandAsync
if (!_pendingCommands.TryAdd(message.Command, inbox))
{
    return new CommandDelivery(CommandDeliveryStatus.AlreadyInFlight, 0, null);
}
```

`ConcurrentDictionary.TryAdd` atomik bir işlemdir. İki istek aynı milisaniyede gelse de sadece biri kazanır. Kilit
yazmamıza gerek kalmaz.

Neden **kuyruğa koymuyoruz** da reddediyoruz? Kuyruktaki ikinci komut birkaç saniye sonra çalışır, ama o arada durum
değişmiş olabilir. Mesela araç inmeye başlamışken kuyruktaki eski bir TAKEOFF çalışırsa operatör bunu hiç
beklemiyordur. Net bir "reddedildi" mesajı, sürpriz bir komuttan iyidir.

**Farklı komutlar birbirini engellemez.** Bir mod değişikliği cevap beklerken RTL hemen gidebilir. RTL'i acil durumda
gönderirsin, onu bekletmek olmaz. (ARM ile DISARM aynı MAV_CMD'yi, yani 400'ü kullanır. Bu yüzden onlar birbirini
bekler.)

## 4. İki operatör aynı aracı yönetirse? Komut kirası (lease)

Senaryo: Ali araca LAND gönderdi. Aynı anda başka bir bilgisayardan Ayşe TAKEOFF gönderdi. Araç hangisini dinlesin?
Bu, gerçek kazaların bilinen bir sebebidir. Çözümümüz **komut kirası** (command lease):

```
Ali:  POST /command-lease     → 200, holder = ali, 60 s
Ayşe: POST /command-lease     → 409 "ali controls this vehicle until 18:51:09 UTC"
Ayşe: POST /commands (LAND)   → 409 command.lease_held   (ve audit'e "Refused" olarak yazılır)
Ali:  DELETE /command-lease   → 204
Ayşe: POST /command-lease     → 200, holder = ayse
```

Kira neden **süreli** (60 s)? Ali'nin bilgisayarı çökerse ne olacağını düşün. Süresiz bir kilit olsaydı araç, biri
fark edip elle açana kadar kilitli kalırdı. Süreli kira kendiliğinden düşer. Ali'nin uygulaması çalıştığı sürece 20
saniyede bir kirayı yeniler. Ayrıca her komut kirayı uzatır.

Kural tek bir yerde, domain'de duruyor:

```csharp
public static Result<CommandLease> Acquire(CommandLease? current, VehicleId vehicleId, OperatorName requester, DateTimeOffset now, TimeSpan duration)
{
    if (current is null || !current.IsActiveAt(now))
        return new CommandLease(vehicleId, requester, now, now + duration);      // boş: al
    return current.Holder == requester
        ? current with { ExpiresAt = now + duration }                            // senin: yenile
        : CommandErrors.LeaseHeldByOther(current.Holder, current.ExpiresAt);     // başkasının: reddet
}
```

Store ise bu "oku → karar ver → yaz" adımlarını tek bir `lock` içinde yapıyor. Böylece iki "kontrolü al" isteği aynı
anda gelse bile ikisi birden boş araç göremez. Buna **check-then-act yarışı** denir ve klasik bir hatadır.

### Kira neden veritabanında değil de bellekte?

İlk akla gelen "PostgreSQL'e yazalım" olur. Ama bağlantılar (UDP soketleri, MAVLink oturumları) zaten tek bir API
işleminin belleğinde yaşıyor. API yeniden başlarsa bağlantılar da kopar. Bağlantısı kopmuş bir araç üzerindeki kira
anlamsızdır. Yeniden başlatmadan sonra herkesin kontrolü tekrar alması da **güvenli varsayılandır**.

Birden fazla API kopyası çalıştırdığımız gün (Phase 11/12) kirayı ortak bir yere (PostgreSQL satır kilidi ya da Redis
`SET NX PX`) taşıyacağız. Bunun için `ICommandLeaseStore` arayüzünü bıraktık. Bu kararın detayı
[ADR-014](../adr/ADR-014-command-lease-ack-and-audit.md)'te.

### X-Operator başlığı güvenlik değildir

Kimlik doğrulama Phase 8'de gelecek. Şimdilik operatör adını `X-Operator: ali` başlığıyla bildiriyoruz. Bu
**tanımlama**dır, **doğrulama** değil: herkes istediği adı yazabilir. Bunu yapmamızın sebebi, kira ve audit
mantığını şimdiden eksiksiz kurup test etmek. Phase 8'de başlığın yerine giriş yapmış kullanıcı gelecek. Handler'lar
ve testler değişmeyecek.

## 5. Denetim kaydı (audit log): "kim, neyi, ne zaman, ne oldu?"

Spec bunu açıkça istiyor: kritik bir işlemi kim yaptı, sonucu ne oldu? Her komut denemesi `gcs.command_audit`
tablosuna bir satır olarak yazılıyor:

| operator | command | parameters | outcome | detail | attempts |
|---|---|---|---|---|---|
| mustafa | Disarm | | Rejected | The vehicle answered Denied. | 1 |
| mustafa | ReturnToLaunch | | Accepted | | 1 |
| ayse | ReturnToLaunch | | Refused | ali controls this vehicle until ... | 0 |
| mustafa | Land | | TimedOut | No COMMAND_ACK after 4 attempts | 4 |

Burada üç tasarım kararı var.

**a) Önce yaz, sonra gönder (write-ahead).** Satır, komut gönderilmeden **önce** `Pending` olarak kaydediliyor,
cevap gelince tamamlanıyor:

```
audit "Pending" (kaydet) ─► COMMAND_LONG ─► ACK/timeout ─► audit "Accepted/Rejected/TimedOut" (kaydet)
```

* Veritabanı çökmüşse komut **hiç gönderilmez**. Kaydı olmayan komut olmaz.
* API komut ortasında çökerse `Pending` satırı "burada bir deneme vardı" diye iz bırakır.

Tersini yapsaydık (önce gönder, sonra yaz), gönderme ile kaydetme arasındaki bir çökme, belki uygulanmış bir komutu
iz bırakmadan kaybederdi.

**b) Reddedilen denemeler de yazılır.** Ayşe'nin kirası olmadan gönderdiği komut da tabloda var. Bir olay
incelemesinde ilk bakılan şey zaten budur: "Kim, yetkisi olmadan neyi denedi?"

**c) Sadece ekleme (append-only), veritabanı zorluyor.** Migration'daki bir trigger şunu yapıyor:

```sql
DELETE FROM gcs.command_audit;
-- ERROR: command_audit is append-only: rows cannot be deleted

UPDATE gcs.command_audit SET outcome = 'Accepted' WHERE ...;   -- tamamlanmış satır
-- ERROR: command_audit row ... is completed and cannot be changed
```

Bir satır sadece bir kez değişebilir: `Pending` → sonuç. Kural kodda da var (`CommandAuditEntry.Complete`), ama
veritabanında da olması, bir bug'ın ya da elle yazılmış bir SQL'in geçmişi değiştirememesi demek. Buna **derinlemesine
savunma** (defense in depth) denir: tek bir katmana güvenme.

Bir de küçük ama önemli bir detay var: komut yola çıktıktan sonra HTTP isteğinin iptalini (`CancellationToken`)
dinlemiyoruz. Operatör pencereyi kapatsa bile audit satırı sonucunu almalı. Bekleme zaten kendi timeout'larıyla
sınırlı.

## 6. Onay (confirmation): hangi komut sorulur, hangisi sorulmaz?

| Komut | Onay | Neden |
|---|---|---|
| ARM | evet | Pervaneler döner, yanında biri olabilir |
| DISARM | evet | Havadaysa araç düşer |
| TAKEOFF | evet | Araç yerden kalkar |
| SET_MODE | evet | Aracın kendi davranışı değişir |
| LAND | **hayır** | Acil durum tepkisi, tek tık olmalı |
| RTL | **hayır** | Acil durum tepkisi, tek tık olmalı |

"Her şeye onay soralım, daha güvenli olur" yanlış bir sezgidir. Araç kontrolden çıkarken önüne bir de "Emin misiniz?"
penceresi çıkarsa güvenli tepkiyi geciktirirsin. Operatörler de sürekli soru soran bir sisteme körlemesine "Evet"
demeyi öğrenir.

Onay iki yerde zorlanıyor:

1. **Masaüstünde:** bir pencere açılıyor. Onay butonu varsayılan buton değil. Refleksle basılan Enter hiçbir şey
   yapmaz, Escape iptal eder.
2. **Sunucuda:** istekte `"confirm": true` yoksa `400 command.confirmation_required` döner. Böylece bir script ya da
   başka bir istemci onayı yanlışlıkla atlayamaz.

## 7. Simülatör artık uçuyor

Eskiden simülatör sadece daire çiziyordu. Artık PX4 gibi davranıyor:

```
Yerde ──ARM──► armed ──TAKEOFF 30──► Kalkıyor (3 m/s) ──► Havada asılı (AUTO.LOITER)
Havada ──LAND──► İniyor (2 m/s) ──► Yerde, kendini disarm ediyor
Havada ──RTL──► Eve uçuyor (12 m/s) ──► İniyor
Havada DISARM ──► DENIED          Disarm iken TAKEOFF ──► DENIED
```

Testler için iki arıza enjekte edebiliyoruz:

* `DropNextCommands = 2` → sonraki iki komut "telsizde kaybolur". Retry testleri bunu kullanıyor.
* `IgnoreCommands = true` → araç komutları alır ama hiç cevap vermez. Timeout testleri bunu kullanıyor.

## 8. Testler: "bitti" ne demek?

Spec'in bitiş kriteri: **timeout ve çift komut testleri geçmeli.** Bunları üç seviyede test ettik.

**Birim testleri** (`CommandExchangeTests`, `MavlinkConnectionTests`):

* İlk iki paket kaybolunca üçüncü denemede kabul ediliyor ve `confirmation` değerleri 0, 1, 2 oluyor.
* Hiç cevap vermeyen araç tam 4 gönderimden sonra TimedOut oluyor.
* DENIED tekrar denenmiyor (1 gönderim).
* Aynı komut yoldayken ikincisi `AlreadyInFlight` alıyor, farklı bir komut (RTL) ise geçiyor.
* `FakeTimeProvider` ile saati elle ilerletiyoruz. 6 saniyelik bir timeout testi milisaniyeler sürüyor.

**Entegrasyon testleri** (`CommandEndpointTests`): gerçek PostgreSQL (Testcontainers), gerçek UDP soketi ve
simülatör.

* Tam uçuş: ARM → TAKEOFF 20 m → havada DISARM (409 rejected) → RTL → araç iniyor. Audit'te 4 satır doğru sırada.
* Cevap vermeyen araç: `504`, simülatöre 3 kopya ulaşmış (confirmation 0, 1, 2), audit'te `TimedOut`, bağlantı
  hâlâ `Connected` (sadece komut başarısız oldu, link değil).
* Aynı anda iki LAND: biri `504`, diğeri `409 in_flight`. Simülatöre 3 paket ulaşmış, 6 değil.
* Ali/Ayşe kira senaryosu.
* `DELETE FROM command_audit` veritabanında hata veriyor.

Kendin çalıştırmak için:

```bash
dotnet test --project tests/Gcs.UnitTests
dotnet test --project tests/Gcs.IntegrationTests -- --filter-class "Gcs.IntegrationTests.Commands.CommandEndpointTests"
```

## 9. Uygulamayı çalıştırınca yakaladığımız hata

Tüm testler yeşildi, ama masaüstü uygulaması açılır açılmaz **StackOverflow** ile çöktü. Sebep şu satırdı:

```xml
<Run Text="{Binding Operator, StringFormat='by {0}'}" />
```

Avalonia'da `Run.Text` bağlaması varsayılan olarak **iki yönlüdür** (TwoWay). Ekrana "by mustafa" yazılınca Avalonia
bunu kaynağa geri yazdı. Operator "by mustafa" oldu, ekranda "by by mustafa" göründü, o da geri yazıldı... sonsuz
döngü. Çözüm `Mode=OneWay`.

Ders: view model testleri XAML bağlamalarını test etmez. "Testler geçti" ile "uygulama çalışıyor" aynı şey değildir.
Bir özelliği bitirmeden önce gerçekten çalıştırıp bak.

## 10. Kendin dene

Docker stack'i çalışırken (`docker compose up -d`), SIM-01'in id'sini alıp:

```bash
id=<SIM-01 id>
curl -X POST localhost:8080/api/v1/vehicles/$id/command-lease -H "X-Operator: ben"

# Havada DISARM: araç reddeder (409, "Denied")
curl -X POST localhost:8080/api/v1/vehicles/$id/commands -H "X-Operator: ben" \
     -H "Content-Type: application/json" -d '{"command":"Disarm","confirm":true}'

# Başka biri kontrolü almaya çalışırsa (409, lease_held)
curl -X POST localhost:8080/api/v1/vehicles/$id/command-lease -H "X-Operator: baskasi"

# Eve dön: araç eve uçar, iner ve kendini disarm eder
curl -X POST localhost:8080/api/v1/vehicles/$id/commands -H "X-Operator: ben" \
     -H "Content-Type: application/json" -d '{"command":"ReturnToLaunch"}'

# Audit
curl localhost:8080/api/v1/vehicles/$id/commands
```

Masaüstünde Flight sekmesindeki **CONTROL** panelinden de aynısını yapabilirsin. Operatör adın varsayılan olarak
Windows kullanıcı adındır, `--operator ali` ile değiştirebilirsin. İki pencere açıp birine `--operator ali`, diğerine
`--operator ayse` ver ve kontrolün birinden diğerine geçişini izle. SignalR ile diğer pencere anında "Controlled by
ali" gösterir.

## 11. Kendine sorular

1. RTL'e neden onay sorulmuyor? ARM'a neden soruluyor?
2. Bir komut 504 döndü. Hemen tekrar göndermek neden her zaman doğru değil?
3. ARM ve DISARM neden aynı anda yolda olamıyor, ama ARM ile RTL olabiliyor?
4. Kira süresi 60 s yerine 1 saat olsaydı hangi sorun çıkardı? 2 saniye olsaydı?
5. Audit satırını komuttan **sonra** yazsaydık hangi senaryoda iz kaybederdik?
6. PX4'e TAKEOFF param7 = 30 gönderseydik ne olurdu?

<details>
<summary>Kısa cevaplar</summary>

1. RTL acil durum tepkisidir, gecikmemeli. ARM pervaneleri döndürür ve yanında biri olabilir.
2. Timeout "bilinmiyor" demektir. Komut belki uygulandı. Önce telemetriye bak.
3. ACK sadece MAV_CMD numarası taşır. ARM ve DISARM ikisi de 400 numarasını kullanır, ACK'leri ayırt edilemez. RTL'in
   numarası (20) farklıdır.
4. 1 saat: çöken bir GCS aracı bir saat kilitler. 2 saniye: küçük bir ağ gecikmesinde operatör kontrolü kaybeder.
5. Komut gönderildi, araç uyguladı, kayıt yazılamadan API çöktü. Geriye hiç iz kalmaz.
6. PX4 bunu deniz seviyesinden 30 m olarak okur. Ankara'da bu yerin yaklaşık 908 m altı demek.

</details>
