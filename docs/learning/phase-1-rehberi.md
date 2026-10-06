# Phase 1 Rehberi: Ne yaptık, nasıl yaptık, neden?

Bu doküman yeni mezun bir mühendis için yazıldı. Her başlıkta önce **ne** olduğunu, sonra **projede nasıl**
kullandığımızı, en son **neden** seçtiğimizi örnekle anlatıyorum. Dosya yollarına tıklayıp kodu yan yana okuyabilirsin.

---

## 0. Büyük resim: Bir benzetme

Yer Kontrol İstasyonunu (GCS) bir **havalimanı kontrol kulesi** gibi düşün:

| Kule | Bizim sistem |
|---|---|
| Uçaklarla telsizle konuşan operatör | `Gcs.Mavlink` (İHA ile MAVLink protokolüyle konuşur) |
| Kuralları bilen şef ("pist doluysa iniş izni verme") | `Gcs.Domain` (iş kuralları) |
| İşleri organize eden müdür ("iniş talebi gelince önce kontrol et, sonra kaydet, sonra haber ver") | `Gcs.Application` (use case'ler) |
| Arşiv dolapları | `Gcs.Persistence` + PostgreSQL |
| Duyuru panosu ("UAV-03 bağlandı") | `Gcs.Messaging` + RabbitMQ |
| Dışarıya açılan danışma bankosu | `Gcs.Api` (REST + SignalR) |
| Kuledeki ekranlar | `Gcs.Desktop` (Avalonia arayüzü) |

Phase 1'in amacı uçakları uçurmak değil, **kulenin binasını, elektriğini, kablolarını ve alarm sistemini kurmaktı.**
Yani henüz "araç ekle" ya da "telemetri göster" gibi özellik yok. Ama her şeyin oturacağı sağlam bir temel var.

---

## 1. Bilgisayara kurduğum / kontrol ettiğim araçlar

| Araç | Durum | Ne işe yarar? |
|---|---|---|
| **Git** 2.55 | Zaten vardı | Kodun versiyon geçmişini tutar. "Dün çalışıyordu, bugün bozuldu, ne değişti?" sorusunun cevabı. |
| **.NET SDK** 10.0.401 | Zaten vardı | C# kodunu derleyen, test eden, paketleyen araç seti. |
| **GitHub CLI (gh)** 2.102 | Ben kurdum (winget) | Terminalden GitHub'da repo açmak, CI sonuçlarına bakmak için. |
| **Docker Desktop** | Ben kurdum (winget) | PostgreSQL, RabbitMQ gibi servisleri bilgisayara tek tek kurmadan "kutu" (container) içinde çalıştırır. |
| **WSL2** | Yok, senin kurman lazım | Docker Desktop, Windows'ta Linux container çalıştırmak için buna ihtiyaç duyar. Yönetici yetkisi ve yeniden başlatma gerektirdiği için ben yapmadım. |

**winget nedir?** Windows'un paket yöneticisi. `winget install GitHub.cli` yazınca indirir, imzasını (hash) doğrular,
kurar. Tarayıcıdan "setup.exe" indirmekten daha güvenli ve tekrarlanabilir.

---

## 2. Solution ve proje yapısı

### Ne?
.NET'te bir **project** (`.csproj`) derlenince bir **assembly** (`.dll` veya `.exe`) çıkar. **Solution**
(`Gcs.slnx`) ise birçok projeyi bir arada tutan dosyadır. `.slnx`, .NET 10 ile gelen yeni XML formatı; eski `.sln`'den
çok daha okunaklı.

```
Ground Control Software/
├── src/                      ← ürün kodu
│   ├── Gcs.Domain/           iş kuralları (hiçbir şeye bağımlı değil)
│   ├── Gcs.Application/      use case'ler, arayüzler (port)
│   ├── Gcs.Contracts/        API'nin dışarıya verdiği veri şekilleri (DTO)
│   ├── Gcs.Persistence/      PostgreSQL erişimi (EF Core)
│   ├── Gcs.Messaging/        RabbitMQ erişimi
│   ├── Gcs.Mavlink/          İHA haberleşmesi (Phase 3)
│   ├── Gcs.Telemetry/        telemetri işleme (Phase 4)
│   ├── Gcs.Infrastructure/   hepsini birbirine bağlayan yer
│   ├── Gcs.Api/              web sunucusu (çalışan program)
│   ├── Gcs.Simulation/       sahte İHA (çalışan program)
│   └── Gcs.Desktop/          operatör arayüzü (çalışan program)
├── tests/                    ← test kodu
├── docs/                     ← dokümantasyon ve ADR'ler
├── docker/                   ← Dockerfile'lar
└── .github/workflows/        ← CI
```

### Neden bu kadar çok proje? → Clean Architecture

Tek bir projeye her şeyi koysaydık çalışırdı. Ama 6 ay sonra şöyle bir kod görürdük:

```csharp
// KÖTÜ: Arayüz butonu doğrudan UDP soketi açıyor ve veritabanına SQL yazıyor
void ArmButton_Click()
{
    var udp = new UdpClient(14550);
    udp.Send(new byte[] { 0xFD, 0x21, ... });          // MAVLink paketini elle yazıyor
    db.Execute("INSERT INTO audit VALUES ('ARM')");   // SQL arayüzün içinde
}
```

Bu kodda: test yazamazsın (gerçek İHA lazım), PX4 yerine ArduPilot gelirse arayüzü değiştirmen gerekir, kimin ARM
komutu verdiğini kontrol eden yetki kuralı bir yerde unutulur.

**Clean Architecture'ın tek kuralı:** Bağımlılıklar **içe doğru** akar. İçteki katman dıştakini bilmez.

```
Desktop ─► Contracts
Api ─► Infrastructure ─► Persistence / Messaging / Mavlink ─► Application ─► Domain
```

- `Domain` hiçbir şeyi bilmez: ne veritabanını ne web'i. Sadece "araç uçarken disarm edilemez" gibi kurallar.
- `Application` "ne yapılacağını" bilir ama "nasıl"ı bilmez. Örneğin `IVehicleRepository` diye bir **arayüz** tanımlar
  ("bana bir araç kaydedecek biri lazım"). Veritabanının PostgreSQL olduğunu bilmez.
- `Persistence` bu arayüzü PostgreSQL ile **uygular** (implement eder).

**Örnek (Phase 2'de yazacağımız şey):**

```csharp
// Application katmanı: sadece sözleşme
public interface IVehicleRepository
{
    Task AddAsync(Vehicle vehicle, CancellationToken ct);
}

// Persistence katmanı: gerçek uygulama
internal sealed class VehicleRepository(GcsDbContext db) : IVehicleRepository
{
    public async Task AddAsync(Vehicle vehicle, CancellationToken ct) => await db.Vehicles.AddAsync(vehicle, ct);
}

// Unit testte: sahte uygulama, veritabanı gerekmez
class FakeVehicleRepository : IVehicleRepository { public List<Vehicle> Items = []; ... }
```

İşte "neden interface kullandık?" sorusunun cevabı: **değiştirilebilirlik ve test edilebilirlik.**

### Neden microservice değil?
Microservice = her parçayı ayrı program olarak ayrı sunucuda çalıştırmak. Telemetri saniyede onlarca kez geliyor;
her mesajın ağ üzerinden servisten servise gitmesi gecikme ve hata noktası demek. Tek kişilik ekip için de operasyon
yükü çok büyük. **Modular monolith** = tek program ama içi sıkı bölümlenmiş. İleride gerekirse bir modülü koparıp
ayrı servis yapabiliriz. (Bkz. [ADR-003](../adr/ADR-003-modular-monolith.md))

---

## 3. Build ayarları: "Kurallar dosyası"

### `Directory.Build.props`
Bu dosya klasördeki **bütün projelere** otomatik uygulanır. 14 projede aynı ayarı 14 kez yazmak yerine bir kez yazdık.
Önemli satırlar:

| Ayar | Ne yapar? | Örnek |
|---|---|---|
| `Nullable=enable` | Derleyici "bu değişken null olabilir" diye uyarır. | `string? name = null; name.Length` → derleme hatası. Çalışma anında `NullReferenceException` yerine derlerken yakalanır. |
| `TreatWarningsAsErrors=true` | Her uyarı hata sayılır, build kırılır. | Kullanılmayan `using` bile build'i kırar. Uyarılar birikip görmezden gelinmez. |
| `AnalysisMode=Recommended` | Microsoft'un kod kalite kuralları (CA ile başlayan) açık. | Build sırasında `CA1822: 'Title' statik yapılabilir` hatası aldım ve düzelttim. |
| `ImplicitUsings` | `System`, `System.Linq` gibi temel namespace'leri her dosyaya otomatik ekler. | |

### `Directory.Packages.props`: Merkezi paket yönetimi
Bütün NuGet paket sürümleri tek dosyada. Projeler sadece "Serilog lazım" der, sürüm yazmaz.

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="Serilog.AspNetCore" Version="10.0.0" />
<!-- Gcs.Api.csproj -->
<PackageReference Include="Serilog.AspNetCore" />
```

**Neden?** Bir projede EF Core 10.0.12, diğerinde 10.0.8 olursa "bende çalışıyor sende çalışmıyor" hataları çıkar.
Güncelleme de tek satır değişikliği olur.

### `global.json`
Hangi .NET SDK sürümüyle derleneceğini sabitler (10.0.401). CI sunucusu da aynı sürümü kurar. Ayrıca testlerin yeni
**Microsoft.Testing.Platform** ile koşacağını burada söyledik (bkz. Bölüm 14).

### `.editorconfig`
Kod stili kuralları: girinti 4 boşluk, private alanlar `_` ile başlar (`_connection`), `if`'lerde süslü parantez
zorunlu vb. Kim yazarsa yazsın kod aynı görünür.

### `.gitignore`
Git'e **eklenmeyecek** dosyalar: `bin/`, `obj/` (derleme çıktısı, her makinede yeniden üretilir), `.env`
(şifreler!), `claude_prompt.txt` (senin özel proje metnin, repoya koymadım).

---

## 4. Domain: `Entity` ve `AggregateRoot`

Dosyalar: [Entity.cs](../../src/Gcs.Domain/Common/Entity.cs), [AggregateRoot.cs](../../src/Gcs.Domain/Common/AggregateRoot.cs)

### Entity nedir?
**Kimliği olan** nesne. İki araç düşün: ikisi de "PX4, 3.2 kg, batarya %80" olabilir ama farklı araçlardır, çünkü
**Id**'leri farklı. Bataryası %20'ye düşse de aynı araçtır, çünkü Id aynı.

```csharp
var a = new Vehicle(id: guid1, name: "UAV-01");
var b = new Vehicle(id: guid1, name: "UAV-01 (yeniden adlandırıldı)");
a == b   // true, çünkü Id aynı. Entity eşitliği Id'ye bakar.
```

Bunun tersi **Value Object**: kimliği yoktur, değerleri aynıysa aynıdır. Örneğin `Position(lat: 39.9, lon: 32.8, alt: 100)`.
İki konum aynı koordinattaysa aynıdır. Bunları Phase 2'de yazacağız.

### AggregateRoot nedir?
Birlikte tutarlı kalması gereken nesneler grubunun "kapısı". Örneğin `Mission` (görev) ve onun `Waypoint`'leri:
kimse dışarıdan doğrudan bir waypoint'i silmemeli, `mission.RemoveWaypoint(...)` çağırmalı. Böylece "görevin ilk
adımı her zaman TAKEOFF olmalı" kuralını `Mission` kontrol edebilir.

### Domain Event nedir?
"Domain'de önemli bir şey oldu" kaydı. Örnek: `VehicleConnected`. Aggregate bunu **yükseltir** (raise), Application
katmanı kayıt başarılı olunca RabbitMQ'ya gönderir. Böylece Domain RabbitMQ'yu hiç bilmez.

```csharp
public void Connect()
{
    State = ConnectionState.Connected;
    RaiseDomainEvent(new VehicleConnected(Id, DateTimeOffset.UtcNow));   // sadece listeye ekler
}
```

---

## 5. API: `Program.cs` satır satır

Dosya: [Program.cs](../../src/Gcs.Api/Program.cs)

```csharp
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();   // (1)
try
{
    var builder = WebApplication.CreateBuilder(args);                        // (2)
    builder.Services.AddSerilog(...);                                        // (3)
    builder.Services.AddProblemDetails();                                    // (4)
    builder.Services.AddApiVersioning(...);                                  // (5)
    builder.Services.AddApplication();                                       // (6)
    builder.Services.AddInfrastructure(builder.Configuration);               // (6)

    var app = builder.Build();
    app.UseMiddleware<CorrelationIdMiddleware>();                            // (7)
    app.UseSerilogRequestLogging();                                          // (8)
    app.MapHealthEndpoints();                                                // (9)
    app.MapSystemEndpoints();                                                // (10)
    await app.RunAsync();
}
catch (Exception ex) { Log.Fatal(ex, "..."); return 1; }                     // (11)
```

1. **Başlangıç logger'ı:** Program açılırken bir hata olursa (örneğin bağlantı cümlesi yok) bunu görebilmek için.
2. **Builder:** Ayarları (appsettings.json, ortam değişkenleri) okur, servis kaydı için hazırlar.
3–6. **Dependency Injection (DI) kaydı.** Aşağıda anlatıyorum.
7–8. **Middleware:** Her HTTP isteğinin içinden geçtiği boru hattı.
9–10. **Endpoint'ler:** URL → kod eşleştirmesi.
11. Program çökerse son sözünü loga yazar ve **0 dışında bir kodla** çıkar (Docker/systemd bunu görüp yeniden başlatabilir).

### 5.1 Dependency Injection (DI): Neden `new` yazmıyoruz?

**Kötü:**
```csharp
class HealthCheck
{
    private readonly RabbitMqConnectionProvider _p = new RabbitMqConnectionProvider("localhost", "gcs", "şifre");
}
```
Şifre koda gömülü, test ederken gerçek RabbitMQ lazım, değiştirmek için her yeri düzeltmek gerekir.

**İyi (bizim yaptığımız):**
```csharp
// Kayıt (bir kez, Messaging/DependencyInjection.cs):
services.AddSingleton<IRabbitMqConnectionProvider, RabbitMqConnectionProvider>();

// Kullanım: sadece istiyor, kimin verdiğini bilmiyor
internal sealed class RabbitMqHealthCheck(IRabbitMqConnectionProvider connectionProvider) : IHealthCheck
```
ASP.NET Core çalışırken "`RabbitMqHealthCheck` bir `IRabbitMqConnectionProvider` istiyor, kayıtlarda
`RabbitMqConnectionProvider` var, onu oluşturup vereyim" der. Testte yerine sahtesini verebiliriz.

**Yaşam süreleri:**
- `Singleton`: Uygulama boyunca **tek** örnek. RabbitMQ bağlantısı böyle; çünkü bağlantı açmak pahalı (TCP + kimlik doğrulama).
- `Scoped`: Her HTTP isteği için **bir** örnek. `DbContext` böyle; çünkü iki isteğin aynı veritabanı oturumunu paylaşması hatalı olur.
- `Transient`: Her istendiğinde **yeni**.

### 5.2 Configuration ve Options doğrulama

Dosyalar: [appsettings.json](../../src/Gcs.Api/appsettings.json), [RabbitMqOptions.cs](../../src/Gcs.Messaging/RabbitMqOptions.cs)

Ayarlar katman katman okunur, sonra gelen öncekini ezer:
```
appsettings.json  →  appsettings.Development.json  →  user secrets  →  ortam değişkenleri
```
Örnek: `appsettings.json`'da `RabbitMq:HostName = localhost`. Docker içinde `RabbitMq__HostName=rabbitmq` ortam
değişkeni verdik (`:` yerine `__`). Docker'da çalışırken `rabbitmq`, kendi bilgisayarında `localhost` olur. Kod hiç değişmez.

Ayarları bir sınıfa bağlayıp **doğruluyoruz**:
```csharp
public sealed class RabbitMqOptions
{
    [Required] public string UserName { get; init; } = string.Empty;
    [Range(1, 65535)] public int Port { get; init; } = 5672;
}
services.AddOptions<RabbitMqOptions>().BindConfiguration("RabbitMq").ValidateDataAnnotations().ValidateOnStart();
```
`ValidateOnStart` sayesinde kullanıcı adı boşsa API **açılırken** hata verip durur. Neden? Hatalı ayarla açılıp
3 saat sonra ilk mesajı gönderirken patlamaktansa, hemen ve net patlamak savunma sistemlerinde tercih edilir: **fail fast**.

**Şifreler:** `appsettings.json`'da şifre yok (boş). Sadece `appsettings.Development.json`'da yerel geliştirme şifresi
(`gcs_dev_password`) var ve bu sadece senin bilgisayarındaki Docker için. Production'da şifreler ortam değişkeni
veya secret store'dan gelecek. Gerçek şifreler asla GitHub'a gitmez.

### 5.3 Serilog ve yapısal (structured) loglama

Normal log:
```
Connected to RabbitMQ at localhost:5672
```
Yapısal log:
```csharp
LogConnected(settings.HostName, settings.Port);
// [LoggerMessage(Message = "Connected to RabbitMQ at {Host}:{Port}")]
```
Ekranda aynı görünür ama arka planda `Host="localhost"`, `Port=5672` **ayrı alanlar** olarak saklanır. Sonra
"Port'u 5672 olmayan bütün bağlantıları göster" gibi sorgular yapılabilir (Phase 9'da Seq/Grafana gibi bir araçla).

`[LoggerMessage]` attribute'u ne? Derleyici bizim için hızlı log kodu üretir (source generator). Telemetri gibi sık
çalışan kodda normal `logger.LogInformation($"...")` string birleştirme yaptığı için yavaştır.

### 5.4 Correlation ID: Bir isteğin izini sürmek

Dosya: [CorrelationIdMiddleware.cs](../../src/Gcs.Api/Middleware/CorrelationIdMiddleware.cs)

**Senaryo:** Operatör "ARM" tuşuna bastı, komut başarısız oldu. Logda aynı saniyede 500 satır var, 3 operatör aynı
anda çalışıyor. Hangisi bu isteğe ait?

Çözüm: her isteğe benzersiz bir kimlik veriyoruz ve **o istek sırasında yazılan her log satırına** ekliyoruz.

```
İstek:  GET /health/ready   Header: X-Correlation-ID: op01-req-42
Log:    [12:01:03 INF] op01-req-42 Gcs.Messaging...: Connected to RabbitMQ at rabbitmq:5672
Log:    [12:01:03 INF] op01-req-42 Serilog...: HTTP GET /health/ready responded 200 in 35 ms
Cevap:  Header: X-Correlation-ID: op01-req-42
```
Artık `op01-req-42` diye arayınca sadece o isteğin satırları gelir. İleride bu kimlik RabbitMQ mesajına ve MAVLink
komutuna da taşınacak: **API → Veritabanı → Broker → İHA** zinciri.

**Güvenlik detayı:** İstemci kendi ID'sini gönderebilir ama biz sadece harf, rakam, `-`, `_`, `.` içeren ve en fazla
64 karakter olanı kabul ediyoruz. Neden? Biri ID olarak `"abc\n[ERR] Sistem hacklendi"` gönderirse loglarımıza sahte
satır enjekte etmiş olurdu (**log injection**). Geçersizse yenisini üretiyoruz. Bunu unit testlerle doğruladık.

### 5.5 Health check: `/health/live` ve `/health/ready`

Dosya: [HealthEndpoints.cs](../../src/Gcs.Api/Endpoints/HealthEndpoints.cs)

| Endpoint | Soru | Ne kontrol eder? |
|---|---|---|
| `/health/live` | "Süreç yaşıyor mu?" | Hiçbir şey. Cevap verebiliyorsa yaşıyordur. |
| `/health/ready` | "Trafik almaya hazır mı?" | PostgreSQL ve RabbitMQ'ya ulaşabiliyor mu? |

**Neden ikiye ayırdık?** Docker veya Kubernetes "live" başarısız olursa programı **yeniden başlatır**. Veritabanı
çöktüğünde live'ı da başarısız yapsaydık, API'yi sürekli yeniden başlatırdı. Sorun API'de değil ki! Bu durumda doğru
davranış: API ayakta kalsın ama "hazır değilim" (HTTP **503**) desin, yük dengeleyici trafiği göndermesin.

Örnek cevap:
```json
{
  "status": "Unhealthy",
  "totalDurationMs": 2004.1,
  "checks": [
    { "name": "postgres", "status": "Unhealthy", "durationMs": 2001.3, "description": null },
    { "name": "rabbitmq", "status": "Healthy",   "durationMs": 3.2,    "description": "RabbitMQ connection is open." }
  ]
}
```

### 5.6 API versiyonlama

`GET /api/v1/system/info` → `{ "service": "gcs-api", "version": "1.0.0", "environment": "Development" }`

**Neden URL'de v1?** Desktop uygulamasının eski sürümü sahada kurulu olabilir. API'yi değiştirdiğimizde (örneğin bir
alanın adını değiştirmek) eski istemci bozulmasın diye `v2` açıp `v1`'i bir süre daha yaşatırız. Cevabın header'ında
`api-supported-versions: 1.0` yazar, istemci hangi sürümlerin desteklendiğini görebilir.

### 5.7 ProblemDetails
Bir hata olduğunda standart (RFC 7807) bir JSON döner:
```json
{ "type": "...", "title": "An error occurred while processing your request.", "status": 500, "traceId": "op01-req-42" }
```
İstemci her hatayı aynı şekilde işleyebilir ve kullanıcıya iç detay (stack trace) gösterilmez.

---

## 6. Persistence: EF Core ve PostgreSQL

Dosyalar: [GcsDbContext.cs](../../src/Gcs.Persistence/GcsDbContext.cs), [DependencyInjection.cs](../../src/Gcs.Persistence/DependencyInjection.cs)

**EF Core** bir **ORM**'dir (Object-Relational Mapper): C# nesnelerini veritabanı tablolarına çevirir.
```csharp
db.Vehicles.Where(v => v.Name == "UAV-01").ToListAsync();
// EF Core bunu SQL'e çevirir: SELECT ... FROM gcs.vehicles WHERE name = 'UAV-01'
```
`DbContext` = veritabanıyla bir "çalışma oturumu". Şu an içinde tablo yok, Phase 2'de `Vehicle` eklenecek.

Yaptığım ayarlar:
- `HasDefaultSchema("gcs")`: Tablolarımız `gcs` şemasında, PostgreSQL'in `public` şemasında karışmasın.
- `EnableRetryOnFailure(3)`: Ağda anlık bir kopma olursa (**transient error**) sorguyu 3 kez yeniden dener. Kalıcı hatalarda (yanlış şifre) denemez.
- `CommandTimeout(30)`: Bir sorgu 30 saniyeden uzun sürerse iptal edilir, sonsuza kadar asılı kalmaz.

**Neden PostgreSQL?** Açık kaynak, lisans ücreti yok, Linux'ta birinci sınıf, transaction desteği güçlü (audit log
için şart), JSONB ile esnek veri de saklayabiliyor. İleride telemetri çok büyürse TimescaleDB eklentisiyle zaman serisi
veritabanına dönüşebiliyor. (Bkz. [ADR-004](../adr/ADR-004-postgresql.md))

---

## 7. Messaging: RabbitMQ

Dosyalar: [RabbitMqConnectionProvider.cs](../../src/Gcs.Messaging/RabbitMqConnectionProvider.cs), [RabbitMqHealthCheck.cs](../../src/Gcs.Messaging/RabbitMqHealthCheck.cs)

**Message broker nedir?** Postane gibi. Gönderen mesajı bırakır, alıcı hazır olduğunda alır. İkisinin aynı anda
çalışması gerekmez.

**Örnek (ileride):** Araç bağlandığında `VehicleConnected` olayı yayınlanır. Audit servisi bunu alıp kaydeder,
bildirim servisi operatöre haber verir. API bu servisleri beklemez, hemen devam eder.

**Önemli karar: Telemetri RabbitMQ'dan GEÇMEYECEK.** Saniyede 50 telemetri mesajını broker üzerinden geçirmek gecikme
ekler. Daha önemlisi: RabbitMQ çökerse operatör aracı göremez hale gelir. Bu bir **güvenlik (safety) sorunu**.
Telemetri program içinde kalır, broker sadece "önemli olaylar" içindir. (Bkz. [ADR-005](../adr/ADR-005-rabbitmq.md))

**Lazy bağlantı:** Bağlantıyı program açılırken değil, **ilk ihtiyaç anında** kuruyoruz:
```csharp
if (_connection is { IsOpen: true } open) return open;   // zaten açıksa onu ver
await _gate.WaitAsync(ct);                               // aynı anda iki thread bağlantı açmasın
try
{
    if (_connection is { IsOpen: true } existing) return existing;   // beklerken başkası açtıysa
    _connection = await factory.CreateConnectionAsync(ct);
}
finally { _gate.Release(); }
```
- **Neden lazy?** RabbitMQ kapalıyken API açılabilsin, ready 503 desin. Broker gelince kendiliğinden bağlansın.
- **`SemaphoreSlim` neden?** 10 istek aynı anda gelirse 10 bağlantı açılmasın diye bir **kilit**. Buna **thread safety** denir.
- **Neden iki kez `IsOpen` kontrolü?** "Double-checked locking" deseni: kilit pahalı olduğu için önce kilitsiz bakarız. Kilidi aldıktan sonra tekrar bakarız, çünkü beklerken başka thread açmış olabilir.

### `async/await` ve `CancellationToken` neden her yerde?
- **async:** Ağdan cevap beklerken thread'i bloklamaz. 1000 eşzamanlı isteği az sayıda thread ile karşılayabiliriz. Örnek: `await factory.CreateConnectionAsync(ct)` beklerken o thread başka isteğe hizmet eder.
- **CancellationToken:** "Vazgeçtim" sinyali. Kullanıcı tarayıcıyı kapattı veya uygulama kapanıyor, o zaman bekleyen işi iptal et. Yoksa kapanış sırasında 30 saniye asılı kalan bağlantılar olur.

---

## 8. Desktop: Avalonia ve MVVM

Dosyalar: [MainWindow.axaml](../../src/Gcs.Desktop/Views/MainWindow.axaml), [MainWindowViewModel.cs](../../src/Gcs.Desktop/ViewModels/MainWindowViewModel.cs)

Şimdilik sadece boş bir pencere ("UAV Ground Control Station" yazısı ve altta durum çubuğu). Asıl arayüz Phase 5'te.

**MVVM = Model / View / ViewModel**
- **View** (`.axaml`): Görünüm, XAML ile. Mantık yok.
- **ViewModel** (`.cs`): Ekranın verisi ve komutları. Ekranı bilmez.
- **Binding:** İkisini bağlar.

```xml
<TextBlock Text="{Binding StatusText}" />
```
```csharp
[ObservableProperty] private string _statusText = "Backend: not connected";
// Kodda StatusText = "Connected" yapınca ekran KENDİLİĞİNDEN güncellenir.
```
`[ObservableProperty]` = CommunityToolkit.Mvvm'in source generator'ı. "Değer değişti" bildirimi kodunu bizim yerimize yazar.

**Neden MVVM?** ViewModel'i ekran açmadan unit test edebiliriz ("bağlantı kopunca StatusText 'Disconnected' oluyor mu?").

**Neden Avalonia?** WPF'e çok benzer ama Windows **ve Linux**'ta çalışır. Sahadaki GCS bilgisayarları genelde Linux.
(Bkz. [ADR-002](../adr/ADR-002-avalonia.md))

---

## 9. Testler

### 9.1 Unit test: AAA deseni
Dosya: [CorrelationIdTests.cs](../../tests/Gcs.UnitTests/Api/CorrelationIdTests.cs)

```csharp
[Theory]
[InlineData("has spaces")]
[InlineData("<script>")]
public void Missing_or_unsafe_id_is_replaced_with_a_new_one(string? incoming)
{
    // Act
    var resolved = CorrelationIdMiddleware.ResolveCorrelationId(incoming);
    // Assert
    resolved.ShouldNotBe(incoming);
    Guid.TryParseExact(resolved, "N", out _).ShouldBeTrue();
}
```
- **Arrange / Act / Assert**: hazırla, çalıştır, kontrol et.
- `[Fact]` = tek senaryo, `[Theory] + [InlineData]` = aynı testi farklı verilerle çalıştır.
- **Shouldly**: `resolved.ShouldNotBe(incoming)` okunaklı assert. Başarısız olursa "resolved should not be 'has spaces' but was" gibi anlaşılır mesaj verir. FluentAssertions yerine bunu seçtim çünkü FluentAssertions 8 artık ticari lisanslı.
- **xUnit v3**: .NET'in en yaygın test çatısının en yeni sürümü.

Toplam 10 unit test metodu, 16 test vakası: Entity eşitliği (4), AggregateRoot olayları (3), Correlation ID (3 metot, `[InlineData]` satırlarıyla 9 vaka).

### 9.2 Architecture test: Katman kuralını makineye denetletmek
Dosya: [LayerDependencyTests.cs](../../tests/Gcs.ArchitectureTests/LayerDependencyTests.cs)

```csharp
[Fact]
public void Desktop_knows_only_public_contracts() =>
    AssertNoDependency(Layers.DesktopAssembly, Layers.Domain, Layers.Application, Layers.Persistence, ...);
```
**Örnek:** 3 ay sonra biri aceleyle Desktop'tan doğrudan `GcsDbContext` kullanırsa bu test **kırılır** ve CI
merge'e izin vermez. Mimari kuralı insanların hafızasına değil, otomatik teste bağladık. 11 kural var. Araç: **NetArchTest**.

### 9.3 Integration test: Gerçek veritabanıyla
Dosya: [GcsApiFactory.cs](../../tests/Gcs.IntegrationTests/Infrastructure/GcsApiFactory.cs)

```
Test başlar
  → Testcontainers Docker'da gerçek PostgreSQL ve RabbitMQ container'ı açar (rastgele portlarda)
  → WebApplicationFactory API'yi bellekte başlatır, bağlantı ayarlarını bu container'lara yönlendirir
  → Test HTTP isteği atar: GET /health/ready
  → Cevap 200 ve iki kontrol de "Healthy" mi?
Test biter → container'lar silinir
```
**Neden sahte (in-memory) veritabanı değil?** Sahte veritabanı PostgreSQL gibi davranmaz. Testler geçer ama
production'da patlar. Gerçek container ile "bende çalışıyor" garantisi gerçek olur.

Bir de **Docker gerektirmeyen** integration testleri yazdım: bağımlılıklar erişilemezken API'nin çökmeden açıldığını,
live=200 ve ready=503 döndüğünü doğruluyor. Defans bakış açısındaki "veritabanı çökerse ne olur?" sorusunun testi.

---

## 10. Docker

### Image ve container farkı
- **Image** = Kalıp (tarif). Örneğin `postgres:18-alpine`: PostgreSQL 18 kurulu minik bir Linux.
- **Container** = O kalıptan çalışan örnek. Aynı image'dan 5 container açabilirsin.

**Neden Docker?** PostgreSQL'i Windows'a kurmak, servis ayarlamak, sürüm çakışmalarıyla uğraşmak yerine
`docker compose up` yaz, her şey aynı sürümle, her bilgisayarda aynı şekilde çalışsın.

### Dockerfile: API'nin image'ı
Dosya: [Gcs.Api.Dockerfile](../../docker/Gcs.Api.Dockerfile)

**Multi-stage build** (iki aşamalı):
1. **build aşaması** (`sdk:10.0`, derleyiciyi içeren büyük image): Kodu derler.
2. **runtime aşaması** (`aspnet:10.0`, sadece çalıştırma ortamı): Sadece derlenmiş çıktıyı alır. Derleyici, kaynak kod içinde yok. Daha küçük, daha güvenli.

**Katman önbelleği hilesi:**
```dockerfile
COPY src/Gcs.Api/Gcs.Api.csproj src/Gcs.Api/     # önce sadece proje dosyaları
RUN dotnet restore ...                            # paketleri indir (yavaş)
COPY src/ src/                                    # sonra kodun tamamı
```
Kodda bir satır değiştirdiğinde paketler değişmediği için Docker restore adımını önbellekten kullanır. Build saniyeler sürer.

**`USER $APP_UID`:** Container içinde program **root olmayan** kullanıcıyla çalışır. Biri API'de bir açık bulsa bile
container içinde yönetici yetkisi olmaz. Temel güvenlik kuralı.

### docker-compose.yml
Dosya: [docker-compose.yml](../../docker-compose.yml)

Dört servis: `postgres`, `rabbitmq`, `redis`, `gcs-api`.

| Ayar | Neden? |
|---|---|
| `healthcheck` | Docker servisin gerçekten hazır olduğunu bilsin. Örnek: `pg_isready` PostgreSQL bağlantı kabul ediyor mu kontrol eder. |
| `depends_on: condition: service_healthy` | API, PostgreSQL ve RabbitMQ **sağlıklı** olana kadar başlamaz. Sadece "başladı" yetmez, "hazır" olmalı. |
| `"127.0.0.1:5432:5432"` | Portlar sadece senin bilgisayarına açık. Aynı Wi-Fi'deki biri veritabanına bağlanamaz. |
| `volumes` | Container silinse de veri kalır. |
| `${POSTGRES_PASSWORD:-gcs_dev_password}` | `.env` dosyasında şifre varsa onu, yoksa yerel varsayılanı kullan. |

**Redis neden var ama kullanılmıyor?** Ortam hazır olsun diye tanımlı. Ama kod Redis'e bağlı değil. Senin metnindeki
kural: "Redis'i gereksiz yere kullanma." Tek API sunucusu varken bellekteki bir sözlük Redis'ten daha hızlıdır ve
ek arıza noktası eklemez. Phase 4'te ölçüp karar vereceğiz. (Bkz. [ADR-008](../adr/ADR-008-redis-deferred.md))

---

## 11. CI: GitHub Actions

Dosya: [ci.yml](../../.github/workflows/ci.yml)

**CI (Continuous Integration)** = Her push'ta ve her Pull Request'te kodu temiz bir sunucuda otomatik derleyip test etmek.

```
push / pull request
   │
   ▼  İş 1: build-and-test (Ubuntu sunucu)
   checkout → .NET kur (global.json'daki sürüm) → restore → build
   → unit test → architecture test → integration test (Testcontainers, sunucuda Docker var)
   │ başarılıysa
   ▼  İş 2: docker
   Docker image build → docker compose up --wait → curl /health/ready → down
```
**Neden?** "Bende çalışıyordu" bahanesini bitirir. main'e merge etmeden testlerin geçmesi şart koşulabilir
(branch protection). İkinci iş, docker-compose dosyasının gerçekten ayağa kalktığını her seferinde kanıtlıyor.

---

## 12. Git ve Conventional Commits

7 commit attım:
```
docs: add README skeleton
docs: add architecture overview and ADR-001 to ADR-008
ci: add github actions pipeline
build: add API dockerfile and docker compose stack
test: add unit, architecture and integration test projects
chore: initialize dotnet solution with clean architecture projects
chore: add repository and shared build configuration
```
Format: `tip: kısa açıklama`. `feat` = yeni özellik, `fix` = hata düzeltme, `test`, `docs`, `ci`, `build`, `chore` = bakım.
**Neden?** Geçmiş okunaklı olur; ileride otomatik sürüm notu (changelog) üretilebilir; iş görüşmesinde GitHub'ına
bakan biri disiplinli çalıştığını görür.

---

## 13. ADR (Architecture Decision Record)

Dosyalar: [docs/adr](../adr/README.md)

"Neden RabbitMQ seçtik?" sorusu 1 yıl sonra sorulduğunda cevap kimsenin aklında olmaz. ADR, her önemli kararın
**bağlamını, alternatiflerini ve sonuçlarını** yazan kısa bir belge. 8 tane yazdım:

| # | Karar |
|---|---|
| 001 | .NET 10 / C# |
| 002 | Avalonia (Windows + Linux masaüstü) |
| 003 | Modular monolith + Clean Architecture |
| 004 | PostgreSQL + EF Core |
| 005 | RabbitMQ sadece domain event'ler için, outbox ile; telemetri broker'dan geçmez |
| 006 | SignalR (canlı veri istemciye) |
| 007 | MAVLink soyutlaması + Asv.Mavlink kütüphanesi |
| 008 | Redis Phase 4'e kadar ertelendi |

---

## 14. Karşılaştığım sorunlar ve çözümleri

Gerçek mühendislik böyle geçer, o yüzden bunları da yazıyorum:

1. **`Gcs.Contracts.System` namespace'i.** Önce sistem bilgisi DTO'sunu bu isimle koydum. Ama içinde `System` adında
   bir namespace olunca, o projedeki `System.String` gibi ifadeler `Gcs.Contracts.System.String`'i aramaya başlar.
   → `Gcs.Contracts.Diagnostics` olarak değiştirdim. **Ders:** Namespace'lere framework isimleri verme.
2. **`ApiVersions` bulunamadı.** `Program.cs` global namespace'te olduğu için `Gcs.Api` namespace'indeki sınıfı
   göremedi. → `using Gcs.Api;` ekledim.
3. **Architecture testlerinde isim çakışması.** `Layers.Domain` sabitini `using static` ile `Domain` diye kullanmak
   istedim. Ama C# önce namespace'lere baktığı için `Domain` = `Gcs.Domain` namespace'i sanıldı.
   → Hepsini `Layers.Domain` olarak açık yazdım.
4. **xUnit v3 + .NET 10 test çalıştırıcısı.** Testler "VSTest artık desteklenmiyor" hatası verdi. xUnit v3'ün yeni
   sürümleri **Microsoft.Testing.Platform** ile çalışıyor. → `global.json`'a `"test": { "runner": "Microsoft.Testing.Platform" }`
   ekledim, eski VSTest paketlerini kaldırdım. Komut artık `dotnet test --project tests/Gcs.UnitTests`.
5. **Smart App Control (çözülmedi, senin kararın).** Windows 11'deki bu güvenlik özelliği **imzasız** her DLL'i
   engelliyor. Bizim derlediğimiz DLL'ler imzasız olduğu için testler, API ve masaüstü uygulaması bu bilgisayarda
   **çalışmıyor** ("Uygulama Denetimi ilkesi bu dosyayı engelledi"). Bir güvenlik ayarı olduğu için kapatmadım.

---

## 15. Şu anki durum

| Kontrol | Sonuç |
|---|---|
| Build (Debug + Release) | ✅ PASS, 0 uyarı |
| Unit testler (10 metot, 16 vaka) | ⏸ Çalıştırılamadı (Smart App Control) |
| Architecture testler (11) | ⏸ Çalıştırılamadı (Smart App Control) |
| Integration testler (8) | ⏸ Çalıştırılamadı (Smart App Control + Docker) |
| docker compose up | ⏸ Denenmedi (WSL2 yok) |
| GitHub repo + CI | ⏸ gh girişi yapılmamış |

### Senden beklenenler
1. **GitHub girişi:** PowerShell'de
   ```powershell
   & "C:\Program Files\GitHub CLI\gh.exe" auth login
   ```
   GitHub.com → HTTPS → "Authenticate Git with your GitHub credentials? Yes" → "Login with a web browser". Ekrandaki
   8 haneli kodu tarayıcıya gir.
2. **WSL2 + Docker:** Yönetici PowerShell'de `wsl --install --no-distribution`, bilgisayarı yeniden başlat, Docker Desktop'ı bir kez açıp kurulumu bitir.
3. **Smart App Control kararı:**
   - **A) Kapat** (Windows Güvenliği → Uygulama ve tarayıcı denetimi → Akıllı Uygulama Denetimi → Kapalı). Bir kez kapatınca Windows'u sıfırlamadan geri açılamıyor. Defender antivirüs açık kalır. Geliştirici bilgisayarlarında yaygın tercih.
   - **B) Açık kalsın**, .NET'i WSL (Ubuntu) içinde çalıştıralım. Masaüstü uygulamasını Windows'ta açmak yine mümkün olmaz.

---

## 16. Kendin dene (öğrenmek için alıştırmalar)

Smart App Control çözüldükten sonra:

1. **Bir architecture testini bilerek kır:** `Gcs.Domain/Common/Entity.cs`'in en üstüne
   `using Microsoft.EntityFrameworkCore;` ekleyip bir yerde kullan, `dotnet test --project tests/Gcs.ArchitectureTests`
   çalıştır. Kırmızıyı gör, sonra geri al.
2. **Fail fast'i gör:** `appsettings.Development.json`'da RabbitMq `UserName`'i boş yap, `dotnet run --project src/Gcs.Api`
   çalıştır. API'nin hangi hatayla durduğunu oku.
3. **Correlation ID'yi gör:** API çalışırken
   ```powershell
   curl.exe -i -H "X-Correlation-ID: mustafa-test-1" http://localhost:5134/health/live
   ```
   Cevap header'ında ve konsol logunda `mustafa-test-1`'i bul. Sonra `-H "X-Correlation-ID: kötü id"` dene.
4. **Ready'yi düşür:** `docker compose stop rabbitmq` yap, `/health/ready`'nin 503'e döndüğünü, `/health/live`'ın 200
   kaldığını gör. `docker compose start rabbitmq` ile geri getir, ready'nin kendiliğinden düzeldiğini izle.
