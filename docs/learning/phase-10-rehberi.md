# Phase 10 Rehberi: Gerçek PX4 ile uçuş (SITL)

Şimdiye kadar bütün MAVLink testlerimiz **kendi simülatörümüzle** konuşuyordu. Bunu şöyle düşün: bir sınavın
sorularını da cevap anahtarını da aynı kişi yazmış. Öğrenci (GCS) her zaman 100 alır, çünkü cevap anahtarındaki hatalar
öğrencinin hatalarıyla birebir aynıdır.

Phase 10'da sınavı başka biri yazdı: **gerçek PX4 otopilotu**. İlk denemede üç hata yakaladı. Aşağıdaki resim,
düzeltmelerden sonraki gerçek uçuştur. GCS'nin kaydettiği telemetri geçmişinden çizildi:

![PX4 SITL uçuşu](../images/px4-sitl-flight-track.svg)

Akış: **Arm → 20 m'ye kalkış → 3 waypoint'lik görev → son bacakta operatör RTL gönderiyor → PX4 eve dönüp iniyor.**
Araç evden 0.5 m sapmayla indi.

---

## 1. SITL nedir? Simülatörden farkı ne?

| Yöntem | Ne çalışıyor? | Fizik | Örnek |
|---|---|---|---|
| **Kendi simülatörümüz** | Bizim yazdığımız sahte araç | Basit kurallar ("3 m/s tırman") | `Gcs.Mavlink.Simulation` |
| **SITL** (Software In The Loop) | **Gerçek otopilot kodu**, ama bilgisayarda | Simüle edilmiş | `px4io/px4-sitl` |
| **HITL** (Hardware In The Loop) | Gerçek uçuş kartı (ör. Pixhawk), masada | Simüle edilmiş | Kart + USB |
| **Gerçek uçuş** | Gerçek kart, gerçek araç | Gerçek dünya | Sahada |

SITL'deki PX4, dronun içinde uçan PX4 ile **aynı C++ kodudur**: aynı mod geçişleri, aynı ön-kontroller (pre-arm
checks), aynı görev protokolü. Değişen tek şey sensörlerdir. GPS, ivmeölçer ve barometre değerlerini gerçek donanım
yerine bir fizik modeli üretir.

Kendi simülatörümüzü çöpe atmıyoruz. İki aracın işleri farklı:

* **Simülatör:** hızlı ve deterministik. "Komutun ilk iki kopyasını kaybet" gibi arızaları istediğimiz an üretebilir.
  Birim testlerinin ve çoğu entegrasyon testinin temeli olmaya devam ediyor.
* **PX4 SITL:** gerçekçi. "Gerçek otopilot bu mesaja ne diyor?" sorusunun tek dürüst cevabı.

## 2. Hangi PX4? Gazebo mu, SIH mi?

PX4'ü simüle etmenin birkaç yolu var. Seçeneklere baktım:

| Seçenek | Boyut | Açılış | Not |
|---|---|---|---|
| PX4'ü kaynak koddan derlemek | ~1.4 GB imaj + 10–15 dk derleme | yavaş | her CI çalışmasında 15 dk kaybederiz |
| Gazebo ile headless imaj | ~3.3 GB | ~30 sn | 3D dünya, kamera ve lidar... GCS bunlara bakmıyor |
| **Resmi `px4io/px4-sitl` (SIH)** | **~50 MB** | **birkaç saniye** | PX4 ekibinin imajı, ekransız çalışacak şekilde tasarlanmış |

**SIH** (Simulation In Hardware), uçuş fiziğini PX4'ün kendi içinde bir modül olarak hesaplar. Ayrı bir simülatör
programı yoktur. Bir GCS'nin test etmesi gereken şey MAVLink davranışıdır: modlar, ACK'ler, görev protokolü, failsafe.
Bunlar için güzel 3D grafiklere gerek yok. 50 MB'lık imaj her pull request'te indirilebilecek kadar ucuz.

**Sürüm sabitleme (pinning):** `FROM px4io/px4-sitl:v1.18.0-rc1`. `latest` yazsaydık, PX4'ün main dalına giren her
commit testlerimizin sonucunu bir gecede değiştirebilirdi. "Dün yeşildi, bugün kırmızı, ama kimse kod değiştirmedi"
durumu tam olarak böyle oluşur. Bu imaj 1.18 geliştirme döneminden beri yayınlanıyor ve henüz kararlı bir 1.18 etiketi
yok. `v1.18.0` çıkınca ona geçeceğiz ([ADR-017](../adr/ADR-017-px4-sitl.md)).

## 3. Docker ağında "127.0.0.1" tuzağı

PX4, GCS'ye giden MAVLink bağlantısını varsayılan olarak `127.0.0.1:14550` adresine gönderir. Kendi bilgisayarında bu
doğru: QGroundControl aynı makinede dinliyor.

Ama bir konteynerin içinde `127.0.0.1` **o konteynerin kendisi** demek. PX4 boşluğa konuşur:

```
┌── px4-sitl konteyneri ──┐        ┌── gcs-api konteyneri ──┐
│ PX4 → 127.0.0.1:14550 ──┼─► ✗    │ API 14560'ı dinliyor    │
│      (kendine gönderir) │        │                         │
└─────────────────────────┘        └─────────────────────────┘
```

Compose'da her servis kendi adıyla bulunabilir: `gcs-api` adı API konteynerinin IP'sine çözülür. Ama PX4'ün `mavlink`
modülü **host adı kabul etmiyor**, sadece IPv4 adresi kabul ediyor. Çözümü küçük bir başlangıç betiği
(`docker/px4-sitl/entrypoint.sh`):

```sh
gcs_ip=$(getent ahostsv4 "$GCS_HOST" | awk '/STREAM/ {print $1; exit}')   # "gcs-api" → 172.18.0.6
sed -i "s|^mavlink start -x -u \$udp_gcs_port_local |mavlink start -x -t $gcs_ip -o $GCS_PORT -u \$udp_gcs_port_local |" "$RC_MAVLINK"
exec /opt/px4/bin/px4 "$@"
```

* `-t 172.18.0.6`: hedef IP (target).
* `-o 14560`: hedef port. Bizim simülatör 14550'yi kullanıyor, çakışmasın diye PX4'e 14560'ı verdik.
* `exec`: betik kendini PX4 ile değiştirir. Böylece `docker stop` sinyali doğrudan PX4'e gider.

Log'da şunu görürsün:

```
INFO  [gcs] MAVLink GCS link -> gcs-api (172.18.0.6):14560/udp
INFO  [mavlink] mode: Normal, data rate: 4000000 B/s on udp port 18570 remote port 14560
```

API tarafında bir şey değişmedi. Phase 3'teki "udpin" mantığı aynen çalışıyor: API 14560'ı dinliyor, PX4 ilk paketi
gönderince API onun adresini (port 18570) öğreniyor ve cevaplarını oraya yolluyor.

### Testlerde: `host.docker.internal`

Entegrasyon testinde API konteynerde değil, **test sürecinin içinde, senin bilgisayarında** çalışıyor. Konteynerin
içinden ana makineye ulaşmanın adı `host.docker.internal`:

* Docker Desktop (Windows/Mac) bu adı kendisi tanımlar.
* Linux'ta (GitHub Actions) tanımlı değildir. Testte `WithExtraHost("host.docker.internal", "host-gateway")` ile
  ekliyoruz. Bu, `docker run --add-host host.docker.internal:host-gateway` ile aynı şey.

### Küçük ama öğretici bir hata: 40 MB log

İlk denemede PX4 konteyneri bir dakikada **41.5 MB** log üretti. Neredeyse hepsi şuydu:

```
pxh> pxh> pxh> pxh> pxh> pxh> ...
```

PX4 açılınca etkileşimli bir kabuk (`pxh>`) açar ve klavyeden komut bekler. Konteynerde klavye yok: stdin kapalı. Kabuk
"boş satır geldi" sanıp sonsuz döngüde yeni bir istem basıyordu. Çözüm, PX4'ü **daemon modunda** (`-d`) başlatmak:

```dockerfile
ENTRYPOINT ["/opt/gcs/entrypoint.sh"]
CMD ["-d"]
```

Ders: bir programı konteynere koyarken "bu program bir terminal bekliyor mu?" diye sor.

### Compose profili

```yaml
px4-sitl:
  profiles: ["sitl"]
```

Profili olan bir servis, `docker compose up` ile **başlamaz**. Sadece istendiğinde başlar:

```bash
docker compose up -d --wait                    # PX4 yok
docker compose --profile sitl up -d --wait     # PX4 de var
```

Çoğu zaman ikinci bir araca ihtiyacın yok. CI'daki compose smoke testinin de PX4 indirmesine gerek yok.

## 4. Senaryo adım adım

Testin ve elle denemenin yaptığı şey aynı. Hepsi public API üzerinden, masaüstü uygulamasının yaptığı gibi:

| Adım | İstek | PX4 log'unda görünen |
|---|---|---|
| 1. Kayıt ve bağlantı | `POST /vehicles` (Udp, 0.0.0.0, 14560, system id 10), `POST .../connection` | |
| 2. Hazır mı? | `GET .../telemetry`: GPS `Fix3D` | `Ready for takeoff!` |
| 3. Kontrolü al | `POST .../command-lease` | |
| 4. Arm | `{"command":"Arm","confirm":true}` | `Armed by external command` |
| 5. Kalkış | `{"command":"Takeoff","altitude":20,"confirm":true}` | `Takeoff detected` |
| 6. Görev | `POST /missions`, `POST /missions/{id}/upload` | |
| 7. Görevi başlat | `{"command":"SetMode","mode":"AUTO.MISSION","confirm":true}` | `Executing Mission` |
| 8. Geri çağır | `{"command":"ReturnToLaunch"}` | `RTL: start return at 968 m` |
| 9. İniş | araç kendisi iner ve disarm olur | `Landing detected`, `Disarmed by landing` |

Kalkışta PX4'ün **deniz seviyesine göre** irtifa beklediğini hatırla (Phase 7). Operatör 20 m yazıyor, GCS ev
irtifasını (938 m) ekleyip 958 m gönderiyor. RTL log'undaki "968 m" de buradan geliyor: PX4 dönüş için 30 m'ye
(938 + 30) tırmanıyor.

## 5. Gerçek otopilotun bulduğu üç hata

En değerli kısım burası. Üç hatanın üçü de simülatörle **asla** görünmezdi, çünkü simülatörü biz yazdık.

### Hata 1: NaN, JSON ve 500

**Belirti:** Araç bağlandı ama `GET /vehicles/{id}/telemetry` her seferinde `500` döndü. Log:

```
System.ArgumentException: .NET number values such as positive and negative infinity cannot be written as valid JSON.
```

**Araştırma:** Hangi alan sonsuz ya da NaN? Bunu bulmak için PX4'ün gönderdiği UDP paketlerini 20 satırlık bir Python
betiğiyle dinledim ve `VFR_HUD` (mesaj 74) içindeki float'ları yazdırdım:

```
VFR nan 0.0061 489.74 0.0
    ↑ airspeed
```

Quadcopter'da **hava hızı sensörü yok**. MAVLink'te float alanlar için "bilinmiyor" demenin yolu NaN göndermek. Bizim
simülatör her zaman bir sayı gönderiyordu.

**Neden 500?** NaN (Not a Number), IEEE 754 float standardında bir değerdir. C#'ta `double.NaN` geçerlidir ama JSON'da
böyle bir şey yoktur. JSON sayıları sadece `12.5` gibi düz değerlerdir. Serializer NaN'ı yazamayınca exception fırlatıyor.

**Düzeltme:** "Bilinmiyor"u NaN ile değil, **null** ile modelle:

```csharp
// Önce
public sealed record MotionState(double GroundSpeed, double AirSpeed, double ClimbRate, double Heading);
// Sonra
public sealed record MotionState(double GroundSpeed, double? AirSpeed, double ClimbRate, double Heading);
```

Çeviri katmanında (MAVLink sınırında):

```csharp
VfrHudMessage hud when AllFinite(hud.Groundspeed, hud.Climb) => new TelemetryUpdate(receivedAt, Motion: new MotionState(
    hud.Groundspeed, float.IsFinite(hud.Airspeed) ? hud.Airspeed : null, hud.Climb, NormalizeHeading(hud.Heading))),
```

* Hava hızı NaN ise `null` olur. API `"airSpeed": null` döner, masaüstü "—" gösterir.
* Zorunlu alanlardan biri (ör. yer hızı) NaN ise mesajın tamamı atılır. 100 ms sonra yenisi gelir.

**Ders:** Dış dünyadan gelen veri **sınırda** temizlenir. NaN'ın JSON'a kadar ilerlemesine izin verirsen her katman
onunla uğraşmak zorunda kalır. `null` ise "bilinmiyor" anlamını tip sisteminde taşır: derleyici seni uyarır.

### Hata 2: Mükemmel bir bağlantıda %76 paket kaybı

**Belirti:** Aynı bilgisayarda, iki konteyner arasında (kayıpsız bir ağ) link kalitesi şöyleydi:

```json
"quality": { "framesReceived": 396, "framesLost": 1272, "packetLossRatio": 0.76 }
```

**Neden?** Her MAVLink çerçevesinin 0–255 arası bir sıra numarası vardır. Kaybı şöyle sayıyoruz: 7'den sonra 10 geldiyse
8 ve 9 kaybolmuştur. Ama tanımadığımız mesajları (`MavlinkMessageRegistry`'de olmayan id'ler) parser'ın atladığı yerde
sıra numarasına **hiç bakmıyorduk**:

```
Gelen:   seq 7 (HEARTBEAT, tanıyoruz)  seq 8 (ESTIMATOR_STATUS, tanımıyoruz)  seq 9 (ATTITUDE, tanıyoruz)
Gördüğümüz:   7 ───────────────────────────────────────────────────────────►  9      → "8 kayboldu!" ✗
```

PX4 yaklaşık 35 farklı mesaj türü gönderiyor, biz bunların bir avucunu çözüyoruz. Simülatörümüz ise sadece bizim
tanıdığımız mesajları gönderdiği için bu durum hiç ortaya çıkmamıştı.

**Düzeltme:** Atlanan çerçevenin de sıra numarasını takip et. O da gelmiş bir çerçeve:

```csharp
if (!MavlinkMessageRegistry.TryGet(messageId, out var info))
{
    Statistics.UnknownMessages++;
    Statistics.TrackSequence(systemId, componentId, sequence);   // yeni satır
    ...
}
```

Sonuç: `framesLost: 0, packetLossRatio: 0`.

**Ders:** Bir metrik yanlışsa, operatör ona güvenmeyi bırakır. "%76 kayıp" gösteren bir ekran, gerçek bir kayıp
olduğunda da görmezden gelinir. Metrikleri de test et.

### Hata 3: Görev güneybatıya uçtu (Null Island)

**Belirti:** Görevi başlattık. İlk waypoint 100 m **kuzeyde** idi, ama araç **güneybatıya** gitmeye başladı. PX4
log'unda şu satır vardı:

```
WARN  [mission_feasibility_checker] First waypoint far away from home: 5548557m. Correct mission loaded?
```

5548 km! Ankara'dan nereye 5548 km? Enlem 0°, boylam 0° noktasına, yani Gine Körfezi'nde, okyanusun ortasındaki
"Null Island"a.

**Neden?** Planlayıcıda kalkış öğesinin konumu yok, anlamı "burada kalk". MAVLink'e çevirirken bunu `0/0` olarak
yazıyorduk:

```csharp
: (0, 0); // 0/0 = "where the vehicle is" for takeoff and land     ← yanlış varsayım
```

`MISSION_ITEM_INT`'te koordinatlar **tamsayıdır** (derece × 10⁷) ve tamsayıda NaN yoktur. ArduPilot 0/0'ı "buradasın"
olarak yorumlar, ama PX4 kelimesi kelimesine alır: "0° K 0° D'ye git". Simülatörümüz ise kalkış öğesinin konumuna hiç
bakmıyordu.

İkinci bir tuzak daha var: görevi geri indirdiğimizde (`GET .../mission`) 0/0 tekrar "konum yok"a çevriliyordu. Yani
gidiş-dönüş testi de yeşil kalıyordu. Hata iki yönde de birbirini örtüyordu.

**Düzeltme:** "Burada"yı **yükleme anında** gerçek koordinata çevir:

* Kalkış "burada" → aracın bildirdiği son konum.
* İniş "burada" → kendinden önceki öğenin konumu (araç oraya vardığında orada olacak).
* Konum gerekiyor ama araç henüz konum göndermediyse yükleme `409 vehicle.mission.position_unknown` ile reddedilir.
  Asla 0/0 gönderilmez.

```csharp
var here = vehiclePosition ?? (0, 0);
foreach (var item in items)
{
    if (item.HasPosition) here = (ToE7(item.Latitude), ToE7(item.Longitude));
    messages.Add(ToMavlink(item, here));
}
```

**Ders:** "0" sihirli bir değer olarak kullanılmamalı. 0 hem geçerli bir koordinat hem de "yok" anlamına geliyorsa,
er geç biri onu yanlış anlar. Burada o "biri" bir otopilottu ve aracı okyanusa yolluyordu.

## 6. Otomatik uçuş testi

`tests/Gcs.IntegrationTests/Sitl/Px4SitlFlightTests.cs` yukarıdaki senaryonun tamamını uçuyor.

### Testcontainers ile kendi imajımız

```csharp
_image = new ImageFromDockerfileBuilder()
    .WithDockerfileDirectory(Path.Combine(RepositoryRoot(), "docker", "px4-sitl"))
    .Build();

_container = new ContainerBuilder(_image)
    .WithExtraHost("host.docker.internal", "host-gateway")
    .WithEnvironment("GCS_HOST", "host.docker.internal")
    .WithEnvironment("GCS_PORT", port.ToString())
    .WithEnvironment("PX4_PARAM_MAV_SYS_ID", systemId.ToString())
    .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Startup script returned successfully"))
    .Build();
```

Compose ile **aynı Dockerfile**'ı kullanıyor. Test ile geliştirme ortamı ayrışmıyor.

`PX4_PARAM_<AD>=<değer>`, PX4'ün kendi başlangıç betiğinin desteklediği bir özellik: her parametreyi ortam
değişkeniyle ayarlayabilirsin. Testte system id'yi her seferinde boş bir değere ayarlıyoruz, çünkü testler aynı
veritabanını paylaşıyor ve system id'ler benzersiz olmalı.

### İsteğe bağlı (opt-in) test

```csharp
Assert.SkipUnless(
    Environment.GetEnvironmentVariable("GCS_PX4_SITL") == "1",
    "PX4 SITL flight test is opt-in: set GCS_PX4_SITL=1 (needs Docker).");
```

Değişken yoksa test **atlandı** (skipped) olarak görünür. Ne başarılı ne başarısız. Neden opt-in?

* Uçuş gerçek zamanda yaklaşık 2 dakika sürüyor. Her `dotnet test`'te 2 dakika beklemek istemezsin.
* İlk çalıştırmada Docker Hub'dan imaj iniyor. İnternet yoksa normal testlerin bundan etkilenmemeli.

```powershell
$env:GCS_PX4_SITL = "1"; dotnet test --project tests/Gcs.IntegrationTests -- --filter-trait "Category=Sitl"
```

### "Kararsız" (flaky) testten ders

İlk çalıştırmada test, `AUTO.MISSION` komutunda **bir kez** kırıldı, sonraki üç çalıştırmada geçti. Sebebi şu:
PX4 yeni bir görev yüklendikten sonra onu kısa bir süre kontrol ediyor ve bu sırada mod değişikliğini
`TemporarilyRejected` ("şimdi değil, birazdan") ile reddediyor. Görev yüklemesi ile mod komutu arasındaki süre bazen
yetiyordu, bazen yetmiyordu.

Yanlış çözüm: `await Task.Delay(5000)`. "5 saniye herkese yeter" varsayımı yavaş bir CI makinesinde çöker. Ayrıca
testi her çalıştırmada boşuna yavaşlatır.

Doğru çözüm: **bir operatörün yapacağını yap**. Reddedilirse 2 saniye bekle, tekrar dene, ama bir üst sınırla:

```csharp
while ((status = await SendAsync(api, command)) != HttpStatusCode.OK)
{
    status.ShouldBe(HttpStatusCode.Conflict);   // sadece "araç reddetti" durumunda tekrar dene
    if (DateTime.UtcNow > deadline) throw new TimeoutException(...);
    await Task.Delay(TimeSpan.FromSeconds(2), Ct);
}
```

Aynı yöntem Arm için de kullanılıyor: PX4 açıldıktan sonra yaklaşık 10 saniye, tahmin algoritması (EKF) yakınsayana
kadar arm'ı reddediyor.

Test **zamanlamayı değil, sonuçları** doğruluyor: mod değişti mi, araç kuzeye gitti mi, evden en fazla 5 m uzağa indi
mi, audit kaydında dört komut "Accepted" mı, paket kaybı %5'in altında mı?

## 7. CI: ayrı bir job, zorunlu değil

```yaml
px4-sitl:
  name: PX4 SITL flight
  needs: build-and-test
  steps:
    - name: Pull the PX4 SITL image        # 4 deneme, artan bekleme
    - name: Fly takeoff, mission and RTL with PX4 SITL
      env:
        GCS_PX4_SITL: "1"
```

Branch koruması sadece iki check'i zorunlu tutuyor (build-and-test ve docker). PX4 job'ı zorunlu **değil**. Neden?
Docker Hub bir saatliğine çökerse kimsenin merge'ü takılmamalı. Ama kırmızı bir PX4 sonucu, gerçek bir regresyon gibi
incelenir. Bir süre kararlı çalıştıktan sonra zorunlu yapılabilir.

## 8. Kendin dene

1. Stack'i PX4 ile başlat: `docker compose --profile sitl up -d --build --wait`
2. Log'a bak: `docker logs gcs-px4-sitl-1`. `MAVLink GCS link -> gcs-api (...)` ve `Ready for takeoff!` satırlarını bul.
3. Masaüstünden `PX4-SITL` aracını kaydet (Udp, host `0.0.0.0`, port `14560`, system id `10`) ve bağlan.
4. Kontrolü al → Arm → Takeoff (20 m). Haritada aracın yükseldiğini izle.
5. Görev planlayıcıda 3 waypoint'lik bir görev çiz, yükle, modu `AUTO.MISSION` yap.
6. Görevin ortasında RTL bas. `docker logs -f gcs-px4-sitl-1` ile PX4'ün ne dediğini canlı izle.
7. Deney: arm ettikten sonra kalkış komutu verme ve 10 saniye bekle. PX4 ne yapıyor? (İpucu: log'a bak.)

## 9. Kendine sorular

1. SITL'deki PX4 ile dronun içindeki PX4 arasındaki fark nedir? Neden SITL testi, kendi simülatörümüzle yapılan
   testten daha değerli?
2. Konteynerin içinden `127.0.0.1:14550`'ye gönderilen bir paket nereye gider?
3. Hava hızını `0` yerine neden `null` yaptık? `0` neyi yanlış ifade ederdi?
4. Tanımadığımız mesajların sıra numarasını takip etmeseydik, 10 tanıdık mesajın arasına 30 tanımadık mesaj giren bir
   akışta kayıp oranı kaç çıkardı?
5. Flaky bir testi `Task.Delay(5000)` ile "düzeltmek" neden kötü bir fikir?
6. PX4 job'ı neden zorunlu bir check değil?

<details>
<summary>Kısa cevaplar</summary>

1. Kod aynıdır, sadece sensörler fizik modelinden gelir. Simülatörü biz yazdığımız için bizim yanlış varsayımlarımızı
   paylaşır. PX4 ise bağımsız bir "cevap anahtarı"dır.
2. Konteynerin kendisine. Başka bir konteynere ulaşmak için servis adı ya da IP, ana makineye ulaşmak için
   `host.docker.internal` gerekir.
3. `0`, "araç hava akımına göre duruyor" demektir ve bu bir ölçümdür. `null` ise "bu aracın hava hızı sensörü yok"
   demektir. Biri bir bilgi, diğeri bilginin yokluğu. Ayrıca NaN JSON'a yazılamaz.
4. 30 / (10 + 30) = %75. Gerçekte %76 gördük.
5. Yavaş bir makinede 5 saniye de yetmeyebilir. Hızlı makinede ise her çalıştırma boşuna 5 saniye bekler. Doğrusu,
   üst sınırı olan bir koşulu beklemek ("kabul edilene kadar, en fazla 15 saniye").
6. Docker Hub gibi dış bir servise bağlı. Dış bir kesinti merge'leri durdurmamalı. Sonuç yine de her PR'da görünür ve
   incelenir.

</details>
