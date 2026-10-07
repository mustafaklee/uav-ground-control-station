# Phase 3 Rehberi: MAVLink, İHA ile ilk konuşma

Phase 3'te GCS ilk kez bir "araçla" konuştu. Bu dokümanda MAVLink'i bayt seviyesinden başlayıp bağlantı yönetimine
kadar, örneklerle anlatıyorum. Teknik referans: [docs/mavlink.md](../mavlink.md), [docs/networking.md](../networking.md).

```
Simülatör / PX4                                        GCS
 ┌────────────┐   UDP datagram (MAVLink 2 frame)   ┌────────────────────────────────────────────┐
 │ HEARTBEAT  │ ─────────────────────────────────► │ UdpMavlinkTransport  (sadece bayt taşır)   │
 │ POSITION   │                                    │ MavlinkFrameParser   (bayt → frame, CRC)   │
 │ ATTITUDE   │ ◄───────────────────────────────── │ MavlinkCodec         (frame → mesaj)       │
 └────────────┘      GCS HEARTBEAT (1 Hz)          │ MavlinkConnection    (heartbeat, watchdog) │
                                                   │ TelemetryTranslator  (1e-7 derece → derece)│
                                                   │ LatestTelemetryStore (son durum, bellekte) │
                                                   │ GET /telemetry       (JSON)                │
                                                   └────────────────────────────────────────────┘
```

---

## 1. MAVLink nedir? Bir paketin anatomisi

MAVLink, İHA'ların ve yer istasyonlarının konuştuğu **ikili (binary)** bir protokol. JSON gibi metin değil, çünkü
telemetri radyoları saniyede birkaç kilobayt taşıyabiliyor; her bayt değerli.

pymavlink'in ürettiği gerçek bir HEARTBEAT paketi (21 bayt):

```
FD 09 00 00 00 01 01 00 00 00 | 00 00 04 03 02 0C 81 04 03 | D3 32
└─────────── başlık ─────────┘ └────────── payload ────────┘ └CRC┘
```

| Bayt | Değer | Anlamı |
|---|---|---|
| `FD` | başlangıç | "MAVLink 2 paketi başlıyor" |
| `09` | 9 | payload uzunluğu |
| `00 00` | | bayraklar (imzasız) |
| `00` | 0 | sıra numarası (sequence) |
| `01` | 1 | system id: hangi araç |
| `01` | 1 | component id: 1 = otopilot |
| `00 00 00` | 0 | mesaj id: 0 = HEARTBEAT |
| `00 00 04 03` | 0x03040000 | custom_mode: PX4'te AUTO (4) + LOITER (3) |
| `02` | 2 | araç tipi: quadrotor |
| `0C` | 12 | otopilot: PX4 |
| `81` | 0b1000_0001 | base_mode: ARMED + custom mode kullanılıyor |
| `04` | 4 | durum: ACTIVE |
| `03` | 3 | MAVLink sürümü |
| `D3 32` | | CRC (sağlama toplamı) |

Dikkat: payload'da alanlar XML'deki sırayla değil, **boyuta göre** sıralı (önce 4 baytlık `custom_mode`, sonra 1
baytlıklar). Bu, tam olarak benim düştüğüm tuzaktı (aşağıda).

### CRC ve CRC_EXTRA

CRC, paketin yolda bozulup bozulmadığını anlamak için. MAVLink'in ilginç bir hilesi var: CRC hesaplanırken sona her
mesaj tipine özel bir **CRC_EXTRA** baytı ekleniyor (HEARTBEAT için 50). Bu bayt mesajın alan tanımından türetiliyor.
Sonuç: biri ATTITUDE mesajının eski bir sürümünü kullanıyorsa CRC tutmaz ve paket reddedilir. **Yanlış okunmaz.**
Uçan bir araçta "yanlış okumak" "hiç okumamaktan" çok daha tehlikelidir.

### Payload truncation (MAVLink 2)

COMMAND_ACK'in payload'ı 3 bayt: `komut (2) + sonuç (1)`. Sonuç "ACCEPTED" = 0 ise son bayt 0'dır ve MAVLink 2
**sondaki sıfırları göndermez**. Paket 2 bayt payload ile gelir. Alıcı eksik baytları 0 kabul etmek zorunda.
`MavlinkCodec.TryDecode` bunu yapıyor, bir test de tam bu durumu kontrol ediyor.

---

## 2. Karar: Kütüphane mi, kendi kodlayıcımız mı?

ADR-007'de Asv.Mavlink seçmiştik. Paketi indirip içine baktım: 3.8 MB'lık bir DLL ve beraberinde Asv.IO, Asv.Cfg,
Asv.Store, ZLogger gibi altı bağımlılık. Yani sadece "kodlayıcı" değil, kendi transport'u, logu ve konfigürasyonu olan
bütün bir framework. Bizim zaten kendi altyapımız var. Bize lazım olan ise ~10 mesaj.

**Kararım:** kendi küçük kodlayıcımızı yazmak, ama doğruluğunu **referans uygulamaya karşı** kanıtlamak
([ADR-010](../adr/ADR-010-own-mavlink-codec.md)).

### Golden test nasıl çalışıyor?

1. `scripts/generate-mavlink-golden.py`, MAVLink projesinin resmi Python kütüphanesi **pymavlink** ile her mesajdan
   örnek paketler üretiyor.
2. Çıktı `GoldenFrames.g.cs` dosyasına bayt dizileri olarak yazılıyor.
3. Testler iki yönü de kontrol ediyor:
   - Bizim `Encode` → pymavlink'in ürettiği **aynı baytlar** mı?
   - pymavlink'in baytlarını bizim `Decode` → **aynı değerler** mi?

Bayt bayt aynıysa, alan sırası, ölçekler, CRC_EXTRA ve truncation **hepsi doğru** demektir.

### Yakalanan hata (gerçek)

Golden script'i ilk yazdığımda SYS_STATUS paketi beklediğimden 2 bayt kısa çıktı. Sebep: pymavlink'in `encode`
fonksiyonu alanları **XML sırasıyla** alıyor, tel üzerindeki sıra ise **boyuta göre**. Ben ilk denemede argümanları
tel sırasıyla vermiştim; batarya yüzdesi yanlış alana gitmişti. Bu tam olarak golden testlerin yakalaması gereken hata
türü, üstelik daha C# kodunu yazmadan yakalandı.

---

## 3. Parser: Gerçek dünya dağınıktır

[MavlinkFrameParser.cs](../../src/Gcs.Mavlink/Protocol/MavlinkFrameParser.cs) şu durumların hepsini ele alıyor (her biri için test var):

| Durum | Örnek | Davranış |
|---|---|---|
| Bir datagram'da birden fazla paket | PX4 bazen birleştirir | hepsi ayrılır |
| Paket parça parça gelir | seri port 1'er bayt verir | parçalar birleştirilene kadar beklenir |
| Arada çöp bayt | radyo gürültüsü | `FD`/`FE` aranarak yeniden senkron |
| Bozuk bayt | CRC tutmaz | paket atılır, sayaç artar, sonraki paket okunur |
| Bilinmeyen mesaj | PX4 yüzlerce tip gönderir | uzunluğu kadar atlanır |
| MAVLink 1 paketi | eski cihaz | o da kabul edilir |

### Paket kaybını ölçmek

Her gönderici her pakette sıra numarasını 1 artırır (255'ten sonra 0). Numaralar 10, 11, 14 diye gelirse 12 ve 13
kaybolmuştur. `MavlinkLinkStatistics` bunu sayıyor; API'de `packetLossRatio` olarak görünüyor.
`byte` aritmetiği 255 → 0 geçişini kendiliğinden doğru hesaplıyor: `(byte)(0 - 255 - 1) = 0` kayıp.

---

## 4. Bağlantı yönetimi: Heartbeat, watchdog, backoff

Dosya: [MavlinkConnection.cs](../../src/Gcs.Mavlink/Connections/MavlinkConnection.cs)

### UDP'de "bağlı olmak" ne demek?

UDP'nin bağlantı kavramı yok. Paket gönderirsin, ulaşıp ulaşmadığını bilemezsin. O yüzden "bağlı" = **araçtan
düzenli HEARTBEAT geliyor** demek. Her MAVLink sistemi saniyede bir HEARTBEAT gönderir. GCS de kendi heartbeat'ini
gönderir (system id 255, component 190), böylece araç da "yer istasyonu hâlâ orada mı?" diye bilebilir. PX4 bunu
"GCS kaybı" failsafe'i için kullanır.

### Oturum döngüsü

```
transport'u aç
 ├─ alma döngüsü:   bayt → frame → mesaj → (heartbeat ise durum güncelle) → telemetriye çevir
 ├─ heartbeat:      her 1 sn GCS HEARTBEAT gönder
 └─ watchdog:       her 250 ms kontrol et:
                      Connecting   ve 10 sn heartbeat yok → oturum bitti
                      Connected    ve son heartbeat 3 sn'den eski → oturum bitti
                      Reconnecting ve 3 sn içinde heartbeat yok → oturum bitti
oturum bitince:
   Connecting   → Faulted ("No heartbeat within 10 s.")       → dur
   Connected    → Reconnecting                                → bekle, tekrar dene
   Reconnecting → deneme sayısı +1; max'a ulaştıysa Faulted   → dur
```

**Neden 3 saniye?** Tek bir kayıp UDP paketi yüzünden "bağlantı koptu" demek istemeyiz. 3 heartbeat üst üste
kaybolursa gerçekten bir sorun vardır.

### Exponential backoff + jitter

Yeniden deneme beklemeleri: 1 sn, 2 sn, 4 sn, 8 sn... (en fazla 30 sn), her birine ±%20 rastgele sapma eklenir.

- **Neden hemen ve sürekli değil?** Zaten zorlanan bir radyo bağlantısını boğarsın, CPU'yu yersin.
- **Neden üstel?** Kısa kopmalar hızlı toparlanır, uzun kopmalarda sistem kendini yormaz.
- **Neden jitter?** Bir röle istasyonu çöktü, 10 araç aynı anda koptu. Jitter olmasa 10'u da tam aynı anda yeniden
  dener. Bu olaya "thundering herd" (sürü etkisi) denir.
- **Neden sınırlı?** Senin metnindeki kural: sonsuz reconnect döngüsü yok. 5 denemeden sonra `Faulted`, sebebi
  yazılı. Operatör tekrar "connect" diyerek elle yeniden dener.

### Thread safety

Durum makinesine üç farklı yerden dokunuluyor: alma döngüsü (heartbeat geldi), watchdog (zaman aşımı) ve API
istekleri (durum sorgula, bağlantıyı kes). Bunlar farklı thread'lerde çalışıyor. Hepsi `lock (_gate)` ile korunuyor.
Kilit içinde sadece hızlı işlemler var (durum değişikliği), ağ işlemi asla kilit içinde yapılmıyor.

---

## 5. Zamanı test etmek: FakeTimeProvider

"3 saniye heartbeat gelmezse Reconnecting olur" nasıl test edilir? Gerçekten 3 saniye beklersek testler yavaşlar ve
yavaş CI makinesinde rastgele düşer.

Çözüm: kod `DateTime.Now` ve `Task.Delay(x)` yerine **`TimeProvider`** kullanıyor. Production'da gerçek saat, testte
`FakeTimeProvider`:

```csharp
await ConnectAsync();                              // heartbeat gönder, Connected
await AdvanceAsync(TimeSpan.FromSeconds(3.5));     // saati 3.5 sn ileri sar (gerçekte milisaniyeler sürer)
_connection.State.ShouldBe(ConnectionState.Reconnecting);
```

"5 deneme ve 1+2+4+8+16 sn bekleme" senaryosu böylece yaklaşık 1 saniyede ve her seferinde aynı sonuçla koşuyor.

### Testin bana öğrettiği şey

Bir test ilk çalıştırmada düştü: "reconnect sırasında heartbeat gelirse bağlantı düzelir". Sebep: backoff
beklemesi sırasında transport kapalı, kimse dinlemiyor. Test tek bir heartbeat gönderip bekliyordu. Gerçek UDP'de
kapalı bir soket paketi düşürür, ama araç her saniye yeniden gönderdiği için bir sonraki deneme yakalar. Kod doğruydu,
test gerçeği yanlış modelliyordu. Testi gerçeğe uygun hale getirdim ve bu davranışı networking.md'ye yazdım.

---

## 6. Simülatör

Dosya: [SimulatedVehicle.cs](../../src/Gcs.Mavlink/Simulation/SimulatedVehicle.cs)

Ankara üzerinde, 150 m yarıçaplı daire çizen, 12 m/s hızla, eve göre 100 m yükseklikte uçan bir PX4 quad.
Değerler **fiziksel olarak tutarlı**:

- Hız vektörü dairenin teğeti (konumun türevi).
- Heading, hız vektörünün yönü (testle doğrulanıyor).
- Yatış açısı (roll) `atan(v² / (r·g))`: bu hız ve yarıçapta dönmek için gereken açı (~5.6°).
- Batarya uçtukça azalıyor, voltaj da onunla birlikte düşüyor.
- ARM/DISARM komutlarına COMMAND_ACK ile cevap veriyor (Phase 7'de kullanacağız).

İki şekilde çalışıyor:

1. **Process içinde:** araç kaydederken `"transport": "Simulator"` seç. Ağ yok, anında çalışır.
2. **Ayrı program olarak (UDP):** `gcs-simulator` container'ı, PX4 SITL'in yapacağı gibi API'nin 14550 portuna
   MAVLink gönderiyor. Gerçek ağ, gerçek soket.

---

## 7. Katmanlar neden böyle?

| Katman | Bilir | Bilmez |
|---|---|---|
| Application (`ConnectVehicleHandler`) | "araca bağlan", "son telemetri" | MAVLink, UDP, paket |
| Gcs.Mavlink | MAVLink, transport, heartbeat | veritabanı, HTTP |
| Gcs.Telemetry | son durumu tutmak | MAVLink |
| API | HTTP | MAVLink, soket |

Application'daki port (`IVehicleLinkManager`) domain dilinde konuşuyor: `ConnectAsync(vehicleId, systemId, connectionSettings)`.
Yarın MAVLink yerine başka bir protokol veya bir radyo modem gelirse sadece Gcs.Mavlink değişir.
Telemetri birimleri tek yerde çevriliyor (`TelemetryTranslator`): enlem tel üzerinde `399250000` (1e-7 derece)
geliyor, sistemin geri kalanı `39.925` görüyor.

---

## 8. Testler

| Proje | Sayı | Phase 3'te eklenenler |
|---|---|---|
| Unit | 162 | golden frame'ler (9 mesaj × 2 yön), parser dayanıklılığı, uçuş modu çözme, birim çevirme, backoff, simülatör fiziği, eşzamanlı telemetri store, bağlantı yaşam döngüsü (sahte saatle) |
| Architecture | 11 | Mavlink, Telemetry'yi bilmiyor; Simulation backend'i bilmiyor |
| Integration | 28 | simülatörle bağlan + tam telemetri; **gerçek UDP**: bağlan → araç sustu → Reconnecting → 2 deneme → Faulted → araç geri geldi → elle bağlan → Connected |

---

## 9. Kendin dene

```powershell
docker compose up -d --build --wait

# Simülatör container'ının taklit ettiği aracı kaydet (system id 1, UDP 14550'yi dinle)
$body = '{"callsign":"SIM-01","mavlinkSystemId":1,"autopilot":"Px4","type":"Multirotor","connection":{"transport":"Udp","host":"0.0.0.0","port":14550}}'
$v = Invoke-RestMethod -Method Post http://localhost:8080/api/v1/vehicles -ContentType "application/json" -Body $body

Invoke-RestMethod -Method Post "http://localhost:8080/api/v1/vehicles/$($v.id)/connection"
Invoke-RestMethod "http://localhost:8080/api/v1/vehicles/$($v.id)/connection"   # state: Connected
Invoke-RestMethod "http://localhost:8080/api/v1/vehicles/$($v.id)/telemetry"    # konum, attitude, batarya, mod

# Bağlantı kopmasını izle:
docker compose stop gcs-simulator
# birkaç kez sorgula: Connected → Reconnecting → (5 deneme sonra) Faulted
docker compose start gcs-simulator
Invoke-RestMethod -Method Post "http://localhost:8080/api/v1/vehicles/$($v.id)/connection"   # elle yeniden bağlan
```

Bir de process içi simülatörü dene: yeni bir araç kaydet, `"connection":{"transport":"Simulator"}` ver ve bağlan.
Ağ olmadan aynı telemetriyi alırsın.
