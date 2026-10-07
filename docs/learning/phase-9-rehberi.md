# Phase 9 Rehberi: Gözlemlenebilirlik (trace, metrik, log, sağlık)

Bir operatör seni arıyor: "DISARM'a bastım, hiçbir şey olmadı." Ne yaparsın?

* İstek API'ye ulaştı mı?
* Kim gönderdi?
* Audit satırı yazıldı mı?
* COMMAND_LONG araca gitti mi, kaç kez denendi?
* Araç ne cevap verdi?

Phase 9'dan önce bu sorulara cevap vermek için log dosyalarında saatlerce arama yapman gerekirdi. Artık tek bir ekran
yetiyor:

![Bir komutun trace'i](../images/observability-trace-command.png)

Bu görüntü gerçek. Docker'daki SIM-01'e, havadayken bir DISARM gönderdim. Bu isteğin trace'i şunu gösteriyor:
HTTP isteği 89 ms sürdü. İçinde iki SQL sorgusu var (araç ve kira kontrolü, ardından audit satırı). Sonra
`vehicle.command Disarm` adımı geliyor: 10 ms sürdü ve **kırmızı ünlem** taşıyor, çünkü araç reddetti. En sonda audit
satırını güncelleyen SQL var. Hepsi tek bir ağaçta.

---

## 1. Üç sinyal: log, metrik, trace

| Sinyal | Soru | Örnek |
|---|---|---|
| **Log** | "Ne oldu?" (tek bir olay) | `Login failed for ali: wrong password` |
| **Metrik** | "Ne kadar, ne sıklıkla?" (sayılar, zaman içinde) | Son 5 dakikada 12 komut zaman aşımına uğradı |
| **Trace** | "Bu istek nereden geçti, nerede ne kadar bekledi?" | Yukarıdaki görüntü |

Üçü birbirini tamamlar. Metrik "bir sorun var" der: zaman aşımı sayısı arttı. Trace "nerede" der: bu araçta, bu
komutta, 4 deneme yapılmış. Log da "neden" der: telsiz bağlantısı o sırada Reconnecting durumundaymış.

## 2. Trace, span ve trace id

**Span**, bir işin bir adımıdır: başlangıcı, bitişi, adı ve etiketleri vardır. **Trace** ise bir isteğe ait span'lerin
ağacıdır. Hepsi aynı **trace id**'yi taşır.

```
Trace 648c44e6...
POST /commands                     89 ms   ← kök span (ASP.NET Core üretir)
├─ postgresql SELECT                1 ms   ← Npgsql üretir
├─ postgresql INSERT command_audit  7 ms
├─ vehicle.command Disarm          10 ms   ← BİZ ürettik
└─ postgresql UPDATE command_audit  4 ms
```

ASP.NET Core ve Npgsql span'lerini kendileri üretiyor. Ama MAVLink'i sadece biz biliyoruz. Bu yüzden kendi span'imizi
açıyoruz:

```csharp
using var activity = GcsTracing.Source.StartActivity($"vehicle.command {command.Type}", ActivityKind.Client);
activity?.SetTag("gcs.vehicle.id", ...);
...
activity?.SetTag("gcs.command.outcome", delivery.Status.ToString());   // Accepted, Rejected, TimedOut
if (delivery.Status != CommandDeliveryStatus.Accepted)
{
    activity?.SetStatus(ActivityStatusCode.Error, delivery.Detail);  // dashboard'da kırmızı ünlem
}
```

.NET'te span'in adı `Activity`'dir. `Activity.Current` her zaman "şu an içinde bulunduğumuz span"ı tutar. Yeni bir
span açınca otomatik olarak onun çocuğu olur. Ağaç böyle kendiliğinden oluşur.

Ayrıca her COMMAND_LONG gönderimi span'e bir **olay** (event) olarak ekleniyor. Bir komut 3 denemede kabul edildiyse,
span'in içinde 3 tane "COMMAND_LONG sent" olayı görürsün: confirmation 0, 1, 2.

### Neden OpenTelemetry'yi doğrudan kullanmıyoruz?

Kendi kodumuz sadece .NET'in yerleşik `System.Diagnostics` sınıflarını kullanıyor (`ActivitySource`, `Meter`).
OpenTelemetry paketleri sadece API projesinde. Bu verileri toplayıp dışarı gönderen o. Böylece domain ve uygulama
katmanları hiçbir izleme ürününe bağımlı değil. Yarın başka bir backend'e geçmek tek bir dosyayı değiştirmek demek.

## 3. En zor kısım: arka plandaki adım (outbox → RabbitMQ)

Bir araç kaydettiğinde şu olur:

1. HTTP isteği gelir. Araç ve `VehicleRegistered` olayı **aynı transaction**'da veritabanına yazılır (outbox, Phase 2).
2. İstek biter, cevap döner.
3. **Saniyeler sonra**, başka bir thread'deki outbox dispatcher olayı RabbitMQ'ya gönderir.

3. adımda `Activity.Current` boştur. İstek çoktan bitti. Yeni bir trace başlasaydı bağlantı kopardı. "Bu mesaj
hangi isteğin sonucu?" sorusunun cevabı kaybolurdu.

Çözüm, trace bilgisini olayla birlikte **veritabanına yazmak**:

```
outbox_messages
| type              | payload | trace_parent                                             |
| VehicleRegistered | {...}   | 00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01  |
```

`traceparent`, W3C'nin standart formatıdır:

```
00 - 4bf92f3577b34da6a3ce929d0e0e4736 - 00f067aa0ba902b7 - 01
sürüm     trace id (16 bayt)              span id (8 bayt)    örneklendi mi
```

Dispatcher olayı gönderirken bu değeri okur ve `publish` span'ini onun **çocuğu** olarak açar. Ardından kendi
`traceparent`'ını RabbitMQ mesajının başlığına koyar. Mesajı tüketen bir servis bu başlığı okursa o da aynı trace'e
katılır. Zincir böylece kopmuyor:

```
HTTP POST /vehicles ─► INSERT ─ ─ ─ (3 sn sonra) ─ ─ ─► VehicleRegistered publish ─► RabbitMQ (traceparent başlığı)
```

Spec'in istediği "Request → Application → Database → Message Broker → MAVLink" zinciri tam olarak bu.

## 4. Gürültüyü atmak: sampler

Outbox dispatcher her saniye veritabanına "bekleyen olay var mı?" diye sorar. Telemetri geçmişi de birkaç saniyede
bir toplu yazar. Bunların her biri ayrı, tek span'lik bir trace olsaydı dashboard'da saatte binlerce anlamsız trace
birikirdi ve gerçek istekler kaybolurdu.

`BackgroundNoiseSampler` şu kuralı uygular: **"Ebeveyni olmayan bir veritabanı ya da HTTP istemci çağrısı (Client
span) kaydedilmez."** Bir isteğin içindeki SQL sorguları ebeveynli olduğu için kalır. Arka plandaki anlamlı iş (olay
yayınlama) önce kendi ebeveyn span'ini açtığı için o da kalır.

```csharp
public override SamplingResult ShouldSample(in SamplingParameters p) =>
    p.Kind == ActivityKind.Client ? Drop : Keep;     // sadece kök span'ler için çağrılır (ParentBasedSampler)
```

Health probe'ları da trace edilmiyor. Docker her 5 saniyede bir soruyor, bu da sadece gürültü olurdu.

## 5. Metrikler

Trace tek bir isteği anlatır, metrik ise sayıları zaman içinde gösterir. Kendi metriklerimiz:

| Metrik | Ne söyler |
|---|---|
| `gcs.commands{command, outcome}` | Hangi komut ne sıklıkla reddediliyor ya da zaman aşımına uğruyor |
| `gcs.command.duration` | Araç ne kadar sürede cevap veriyor. Yeniden denemeler uzun kuyruk olarak görünür |
| `gcs.mavlink.frames{vehicle}` | Araç başına mesaj hızı. Düşüyorsa telsiz bağlantısı zayıflıyordur |
| `gcs.links{state}` | Kaç araç bağlı, kaçı kopuk |
| `gcs.auth.logins{result}` | `invalid_credentials` sayısı birden fırlarsa biri şifre deniyordur |

Bir sayaç böyle artırılıyor:

```csharp
_metrics.Commands.Add(1, new TagList { { "gcs.command", "Disarm" }, { "gcs.command.outcome", "Rejected" } });
```

`IMeterFactory` ile oluşturduğumuz için her test sunucusunun kendi metrikleri oluyor ve testler birbirini
etkilemiyor. Dinleyen kimse yoksa `Add` çağrısı neredeyse hiçbir şeye mal olmuyor.

## 6. Sağlık izleme: üç seviye

| Endpoint | Soru | Cevap verirken |
|---|---|---|
| `/health/live` | "Process ayakta mı?" | Hiçbir şeye bakmaz. Hayır derse Docker yeniden başlatır |
| `/health/ready` | "Bana istek gönderebilir misin?" | PostgreSQL + RabbitMQ |
| `/health/details` | "Operasyonel olarak ne durumda?" | + araç bağlantıları, outbox birikmesi, telemetri geçmişi |

Yeni üç kontrol **Degraded** döner, **Unhealthy** değil. Neden? Bir aracın bağlantısı koptu diye API'yi yeniden
başlatmak hiçbir şeyi düzeltmez, hatta diğer araçları da koparır. Degraded şu anlama gelir: "Ben çalışıyorum, ama izleme
sistemi bir uyarı versin."

```json
{ "name": "vehicle-links", "status": "Degraded",
  "description": "1 of 2 link(s) are not connected.",
  "data": { "01a1...": "Faulted: No heartbeat within 10 s." } }
```

Tek bir istisna var: outbox'ta 10 dakikadan uzun bekleyen bir olay **Unhealthy** (503) sayılıyor. Diğer sistemler o
kadar süredir haber alamıyor demektir.

`/health/details` filo hakkında bilgi verdiği için giriş istiyor (Phase 8'deki `read` izni).

## 7. Loglar trace'e bağlı

Konsoldaki her satırda artık trace id var:

```
[19:48:53 INF] 0HNG4... 648c44e68fcf31442f2c379aff4c5ea9 MavlinkConnection: command Disarm -> Rejected after 1 attempt(s)
               ^korelasyon id  ^trace id
```

Bir log satırındaki trace id'yi kopyalayıp dashboard'da açarsın, o isteğin bütün ağacını görürsün. Ya da tersi: bir
span'e tıklarsın, onun loglarını görürsün. OTLP açıkken loglar da dashboard'a gidiyor.

## 8. Aspire Dashboard

Docker Compose'a tek bir container ekledik: `aspire-dashboard`. OpenTelemetry verisini (OTLP) alır ve trace, metrik
ve logları gösterir.

```bash
docker compose up -d --build --wait
# GCS'yi kullan, sonra:
http://localhost:18888
```

Neden Jaeger + Prometheus + Grafana değil? Onlar üretim için doğru seçim, ama bir geliştirici laptopunda üç container
ve bir sürü yapılandırma demek. Dashboard tek container ve sıfır ayar. API standart OTLP konuştuğu için üretimde
(Phase 11) hiçbir kod değiştirmeden bir OpenTelemetry Collector'a ve oradan kalıcı backend'lere yönlendirebiliriz.

> Dashboard anonim erişime açık. Bu, sadece `127.0.0.1`'e bağlı olduğu için kabul edilebilir. Ağa açılmamalı.

## 9. Testler: "zincir gerçekten kopmuyor mu?"

Entegrasyon testi trace'i **kendisi başlatıyor**. İsteğe kendi uydurduğu bir trace id ile `traceparent` başlığı
ekliyor, sonra her durakta o id'yi arıyor:

```csharp
var traceId = ActivityTraceId.CreateRandom();
request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
await _admin.SendAsync(request);

// Aynı trace id ile: HTTP span'i, en az 2 SQL span'i ve "vehicle.command Land" span'i (outcome = Accepted)
```

Outbox testi de aynı fikirde: aracı kendi trace id'siyle kaydediyor, RabbitMQ'dan mesajı okuyor ve mesajın
`traceparent` başlığında **aynı trace id**'nin olduğunu doğruluyor.

Diğerleri:

* Sağlık testi: kimsenin konuşmadığı bir UDP aracına bağlanılıyor. `/health/details` bu aracı adıyla Faulted olarak
  gösteriyor. Anonim istek 401 alıyor.
* Metrik testi: `MeterListener` ile havada DISARM gönderiliyor ve `gcs.commands{Disarm, Rejected}` ölçümü yakalanıyor.
* Birim testleri: sampler kuralı, sağlık kontrollerinin eşikleri.

```bash
dotnet test --project tests/Gcs.IntegrationTests -- --filter-class "Gcs.IntegrationTests.Observability.ObservabilityTests"
```

## 10. Kendin dene

1. `docker compose up -d --build --wait`
2. Masaüstünden `mustafa` ile gir, SIM-01'in kontrolünü al. Önce havadayken **DISARM** gönder (reddedilecek), sonra
   **RTL** gönder.
3. http://localhost:18888 → **Traces** (İzlemeler). Komut isteğine tıkla. DISARM kırmızı, RTL yeşil görünür.
4. **Metrics** → `gcs.commands`: Rejected ve Accepted ayrı çizgiler olarak görünür.
5. **Structured logs**: `command Disarm -> Rejected` satırına tıkla ve trace'ine git.
6. Bir aracı Disconnect et, sonra `/health/details`'e bak (token gerekir, [security.md](../security.md)).

## 11. Kendine sorular

1. Log, metrik ve trace'in her biri hangi soruya cevap verir? "Komutlar son 1 saatte ne sıklıkla zaman aşımına
   uğradı?" sorusu hangisiyle cevaplanır?
2. Outbox dispatcher'ın publish span'i, neden `traceparent`'ı veritabanından okumak zorunda?
3. Sampler neden kökü "Client" türünde olan span'leri atıyor? Bir isteğin içindeki SQL sorguları neden atılmıyor?
4. Bir aracın bağlantısı koptuğunda `/health/ready` neden hâlâ "Healthy" diyor?
5. Kendi kodumuz neden OpenTelemetry paketini değil de `System.Diagnostics`'i kullanıyor?

<details>
<summary>Kısa cevaplar</summary>

1. Log: tek bir olay. Metrik: sayılar ve eğilimler. Trace: tek bir isteğin yolu. Saatlik zaman aşımı oranı bir
   **metrik** sorusudur (`gcs.commands{outcome=TimedOut}`).
2. Publish saniyeler sonra başka bir thread'de çalışır. O anda `Activity.Current` boştur. İstekten kalan tek iz,
   olayla birlikte kaydettiğimiz `traceparent`'tır.
3. Ebeveyni olmayan bir DB çağrısı arka plan döngüsünden gelir: her saniye bir sorgu, yani gürültü. Bir isteğin
   içindeki sorgular kök değildir, ebeveynlerinin kararını izlerler (ParentBasedSampler).
4. Ready, "bu instance'a istek gönderilebilir mi?" sorusudur. Bir araç koptu diye API'yi trafikten çıkarmak ya da
   yeniden başlatmak durumu düzeltmez. Bu bilgi Degraded olarak `/health/details`'te durur.
5. Domain ve uygulama katmanları bir ürüne bağımlı olmasın diye. Verinin nereye gideceğine sadece API karar verir.

</details>
