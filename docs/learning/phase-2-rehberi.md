# Phase 2 Rehberi: Vehicle Domain

Phase 1'de binayı kurmuştuk. Phase 2'de ilk gerçek "oda"yı yaptık: **araç kaydı**. Artık bir İHA'yı sisteme
tanımlayabiliyor, listeleyebiliyor, güncelleyebiliyor ve hizmetten çıkarabiliyoruz. Bu dokümanda her parçanın
**ne** olduğunu, **nasıl** yazıldığını ve **neden** öyle yazıldığını örneklerle anlatıyorum.

Bir isteğin baştan sona yolculuğu:

```
POST /api/v1/vehicles  {"callsign":"uav-01", ...}
   │
   ▼  Gcs.Api            VehicleEndpoints.RegisterAsync     HTTP'yi çevirir, başka hiçbir şey yapmaz
   ▼  Gcs.Application    RegisterVehicleHandler              doğrula → benzersiz mi? → aracı oluştur → kaydet
   ▼  Gcs.Domain         Vehicle.Register(...)               kurallar + VehicleRegistered olayı
   ▼  Gcs.Persistence    GcsDbContext.SaveChangesAsync       TEK transaction: vehicles + outbox_messages
   ▼  PostgreSQL
   ⋮  (arka planda, birkaç yüz ms sonra)
   ▼  Gcs.Infrastructure OutboxDispatcher                    outbox'tan oku → RabbitMQ'ya gönder → işaretle
   ▼  RabbitMQ           exchange "gcs.events", routing key "vehicle.registered"
```

---

## 1. Domain: İş kuralları

### 1.1 Value Object'ler: "Geçersiz değer var olamaz"

Dosyalar: [Callsign.cs](../../src/Gcs.Domain/Vehicles/Callsign.cs), [MavlinkSystemId.cs](../../src/Gcs.Domain/Vehicles/MavlinkSystemId.cs), [ConnectionSettings.cs](../../src/Gcs.Domain/Vehicles/ConnectionSettings.cs)

Kötü yaklaşım: her yerde `string callsign` dolaştırmak ve "acaba biri kontrol etti mi?" diye düşünmek.

İyi yaklaşım: `Callsign` diye bir tip. Constructor'ı **private**, tek yol `Callsign.Create(...)`:

```csharp
var ok  = Callsign.Create("uav-01");   // Success → Value = "UAV-01" (büyük harfe çevrildi)
var bad = Callsign.Create("UAV 01");   // Failure → Error.Code = "vehicle.callsign.format"
```

Elinde bir `Callsign` nesnesi varsa, **kesinlikle geçerlidir**. Kontrol tek yerde, bir kez yapıldı.
Bunun adı **"make illegal states unrepresentable"** (geçersiz durumu temsil edilemez yap).

- `MavlinkSystemId` neden 1–255? MAVLink paketinde system id 1 byte. 0 da "broadcast" (herkese) anlamına geliyor,
  yani tek bir aracı gösteremez.
- `ConnectionSettings` neden tek tip? UDP için host+port, seri port için port adı+baud rate lazım. Factory
  metotları (`Udp(...)`, `Serial(...)`) sayesinde "port'u olmayan UDP bağlantısı" oluşturmak **mümkün değil**.
- `VehicleId` neden düz `Guid` değil? `void Assign(Guid vehicleId, Guid missionId)` fonksiyonuna parametreleri ters
  verirsen derleyici fark etmez. `VehicleId` ve (ileride) `MissionId` farklı tipler olunca derleyici yakalar.
  `Guid.CreateVersion7()` zamana göre sıralı Guid üretir; veritabanı index'ine hep sona eklendiği için hızlıdır.

### 1.2 Result pattern: Beklenen hatalar exception değildir

Dosyalar: [Result.cs](../../src/Gcs.Domain/Common/Result.cs), [Error.cs](../../src/Gcs.Domain/Common/Error.cs), [VehicleErrors.cs](../../src/Gcs.Domain/Vehicles/VehicleErrors.cs)

Kullanıcının yanlış callsign girmesi bir **hata değil, beklenen bir durum**. Exception fırlatmak hem yavaş
hem de "gizli kontrol akışı" oluşturur: fonksiyonun imzasına bakınca patlayabileceğini görmezsin.

```csharp
Result<Callsign> Create(string? value)   // imza açıkça "başarısız olabilirim" diyor
```

Exception'lar **gerçek arızalar** için kalıyor: veritabanı çöktü, programcı hatası.

Her hatanın **sabit bir kodu** var (`vehicle.callsign.in_use`). Mesaj metni değişebilir, kod değişmez.
Masaüstü uygulaması bu koda bakıp "Bu çağrı adı başka bir araçta kullanılıyor" diye Türkçe mesaj gösterebilir.

### 1.3 Vehicle aggregate ve versiyon

Dosya: [Vehicle.cs](../../src/Gcs.Domain/Vehicles/Vehicle.cs)

```csharp
var vehicle = Vehicle.Register(callsign, systemId, AutopilotType.Px4, VehicleType.Multirotor, connection, now);
// Status = Active, Version = 1, DomainEvents = [VehicleRegistered]

vehicle.Update(..., expectedVersion: 1, now);   // başarılı → Version = 2, [VehicleUpdated]
vehicle.Update(..., expectedVersion: 1, now);   // VersionMismatch: sen 1'i düzenliyordun, şu an 2
vehicle.Retire(null, now);                      // Status = Retired, Version = 3
vehicle.Update(..., expectedVersion: 3, now);   // Retired: emekli araç değiştirilemez
```

**Neden silmek yerine "Retired"?** Audit log'da "operator01, UAV-03'e ARM verdi" kaydı olacak. Aracı fiziksel
olarak silersek o kayıt boşlukta kalır. Buna **soft delete** denir.

**Hiçbir şey değişmediyse** versiyon artmıyor ve olay yayınlanmıyor. Gereksiz "VehicleUpdated" gürültüsü olmasın diye.

### 1.4 Bağlantı durum makinesi

Dosyalar: [ConnectionStateMachine.cs](../../src/Gcs.Domain/Vehicles/Connections/ConnectionStateMachine.cs), [VehicleConnection.cs](../../src/Gcs.Domain/Vehicles/Connections/VehicleConnection.cs)

**State machine** = "hangi durumdan hangi duruma geçilebilir" tablosu:

| Şu an | Gidebileceği yer |
|---|---|
| Disconnected | Connecting |
| Connecting | Connected, Faulted, Disconnected |
| Connected | Reconnecting, Disconnected |
| Reconnecting | Connected, Faulted, Disconnected |
| Faulted | Connecting, Disconnected |

Tabloda olmayan her geçiş bir **bug**'dır. Örneğin "Disconnected → Connected": bağlanmayı hiç istemedik ama
heartbeat geldi. Kod bunu reddeder.

**Sonsuz reconnect döngüsü yok.** Senin metnindeki kural buydu. `MaxReconnectAttempts` sayısına ulaşınca bağlantı
`Faulted` olur ve operatörün karar vermesi gerekir. Bu sınıf veritabanına **yazılmıyor**: saniyede değişen ve
yeniden başlatmadan sonra anlamsız olan bir bilgi. Phase 3'te MAVLink katmanı bunu kullanacak.

---

## 2. Application: Use case'ler

### 2.1 Handler'lar: Neden MediatR yok?

Dosyalar: [RegisterVehicleHandler.cs](../../src/Gcs.Application/Vehicles/RegisterVehicleHandler.cs), [UpdateVehicleHandler.cs](../../src/Gcs.Application/Vehicles/UpdateVehicleHandler.cs)

Her use case bir sınıf, bir `HandleAsync` metodu. Pek çok projede bunun için **MediatR** kütüphanesi kullanılır.
Ama MediatR 2025'te ticari lisansa geçti ve bizim ihtiyacımız olan şey (DI ile handler çağırmak) zaten bedava.
Daha az sihir, daha kolay debug.

Register handler'ın adımları:
1. **Doğrula** (FluentValidation): hepsi bir kerede, alan alan.
2. **Benzersiz mi?** Aktif araçlarda aynı callsign/system id var mı?
3. **Oluştur**: `Vehicle.Register(...)`
4. **Kaydet**: `unitOfWork.SaveChangesAsync()`
5. **Yarış durumu**: 2. adımdan sonra başka bir istek aynı callsign'ı kaydettiyse veritabanının unique index'i
   reddeder. Bunu yakalayıp yine `CallsignInUse` döneriz.

### 2.2 Race condition: "Kontrol ettim, ama..."

Bu Phase 2'nin en öğretici kısmı. İki operatör **aynı milisaniyede** "UAV-07" kaydetmeye çalışıyor:

```
İstek A: "UAV-07 kullanılıyor mu?" → Hayır          İstek B: "UAV-07 kullanılıyor mu?" → Hayır
İstek A: INSERT UAV-07 → OK                          İstek B: INSERT UAV-07 → ??? 
```

Uygulama kontrolü ikisini de geçirdi! Çözüm: **veritabanı son savunma hattı**. `ux_vehicles_callsign_active`
unique index'i B'nin INSERT'ünü reddeder (PostgreSQL hata kodu `23505`). `UnitOfWork` bunu
`UniqueConstraintViolationException`'a çevirir, handler da `409 Conflict`'e.

**Bunu test ettik:** [VehicleEndpointTests](../../tests/Gcs.IntegrationTests/Vehicles/VehicleEndpointTests.cs)'te
8 isteği aynı anda gönderiyoruz. Sonuç: tam olarak 1 tane `201`, 7 tane `409`.

### 2.3 FluentValidation + domain kuralları = tek kaynak

Dosya: [VehicleFieldsValidator.cs](../../src/Gcs.Application/Vehicles/VehicleFieldsValidator.cs)

Validator kendi kuralını **yazmıyor**, domain'i çağırıyor:

```csharp
RuleFor(x => x.Callsign).Custom((value, context) => AddIfFailed(context, Callsign.Create(value)));
```

Callsign kuralını değiştirmek istersen sadece `Callsign.cs`'yi değiştirirsin. İki yerde ayrı kural olsaydı
bir gün birbirinden kayardı. Kullanıcı tüm hatalarını tek seferde görür:

```json
{ "code": "validation.failed",
  "errors": { "callsign": ["Callsign must be between 3 and 32 characters."],
              "connection": ["Port must be between 1 and 65535."] } }
```

### 2.4 Read side ayrı: IVehicleQueries

Yazarken aggregate'i yükleyip kurallarını çalıştırıyoruz (`IVehicleRepository`). Okurken ise kural yok, sadece veri
lazım. `IVehicleQueries` `AsNoTracking()` ile okur: EF Core değişiklik takibi yapmaz, daha hızlıdır.
Bu ayrımın büyük versiyonuna **CQRS** denir. Biz hafif halini kullandık.

---

## 3. Persistence: PostgreSQL

### 3.1 EF Core eşleştirmesi

Dosya: [VehicleConfiguration.cs](../../src/Gcs.Persistence/Vehicles/VehicleConfiguration.cs)

| Domain | Veritabanı kolonu | Nasıl? |
|---|---|---|
| `VehicleId` | `id uuid` | value converter: `id => id.Value` |
| `Callsign` | `callsign varchar(32)` | value converter |
| `MavlinkSystemId` | `system_id smallint` | value converter |
| `AutopilotType.Px4` | `autopilot = 'Px4'` | enum → string (okunabilir, sırası değişse de bozulmaz) |
| `ConnectionSettings` | `connection_transport`, `connection_host`, ... | **complex type**: ayrı tablo yok, aynı satırda kolonlar |
| `Version` | `version integer` | **concurrency token** |

Kolon isimleri `snake_case` (PostgreSQL geleneği) çünkü `EFCore.NamingConventions` paketi C#'taki `SystemId`'yi
otomatik `system_id` yapıyor.

Karşılaştığım sorun: EF Core sadece setter'ı olan property'leri otomatik eşliyor. `ConnectionSettings.Port`
get-only olduğu için atlandı ve "constructor parametresi bağlanamadı" hatası aldım. Hepsini açıkça eşleyerek çözdüm.

### 3.2 Optimistic concurrency nasıl çalışıyor?

`Version` concurrency token olunca EF Core UPDATE'i şöyle yazar:

```sql
UPDATE gcs.vehicles SET type = 'Vtol', version = 2 WHERE id = '...' AND version = 1;
```

Arada başkası kaydettiyse `version` artık 2'dir, `WHERE` hiçbir satırı bulamaz, 0 satır güncellenir. EF Core bunu
fark edip `DbUpdateConcurrencyException` fırlatır. Kimse kilitlenmez, kimsenin değişikliği sessizce ezilmez.

**"Optimistic" neden?** "Çakışma nadirdir" diye iyimser varsayar; kilit tutmaz, çakışma olursa yakalar.
"Pessimistic" (kilitli) yaklaşım, operatör formu açık bıraktığı sürece satırı kilitlerdi. Kötü fikir.

### 3.3 Partial unique index

```sql
CREATE UNIQUE INDEX ux_vehicles_callsign_active ON gcs.vehicles (callsign) WHERE status = 'Active';
```

Benzersizlik **sadece aktif araçlar** için. Emekli UAV-01 dururken yeni bir UAV-01 kaydedilebilir.

### 3.4 Migration'lar

Dosya: [Migrations/](../../src/Gcs.Persistence/Migrations/), karar: [ADR-009](../adr/ADR-009-database-migrations.md)

Migration = şema değişikliğinin sürümlü dosyası. `InitialVehicles` migration'ı `vehicles` ve `outbox_messages`
tablolarını oluşturuyor. Yeni bir kolon eklersek `dotnet ef migrations add AddX` yeni bir dosya üretir; PR'da
SQL'i görürsün.

- **Development**: API açılırken uygular (`ApplyMigrationsOnStartup=true`).
- **Docker**: `gcs-migrator` container'ı **bundle** (tüm migration'ları içeren tek bir çalıştırılabilir dosya)
  çalıştırıp çıkar. API ancak o **başarıyla bittikten sonra** başlar (`service_completed_successfully`).
  3 API kopyası aynı anda şemayı değiştirmeye çalışmaz.

---

## 4. Outbox: Olay asla kaybolmaz

Dosyalar: [GcsDbContext.cs](../../src/Gcs.Persistence/GcsDbContext.cs), [OutboxStore.cs](../../src/Gcs.Persistence/Outbox/OutboxStore.cs), [OutboxDispatcher.cs](../../src/Gcs.Infrastructure/Outbox/OutboxDispatcher.cs), [RabbitMqEventPublisher.cs](../../src/Gcs.Messaging/RabbitMqEventPublisher.cs)

### Problem: "Dual write"

```csharp
await db.SaveChangesAsync();          // 1. araç kaydedildi
await rabbit.PublishAsync(event);     // 2. ← tam burada elektrik kesilirse?
```

Araç var ama "VehicleRegistered" olayı hiç gitmedi. Audit servisi bu aracı hiç duymadı. Sırayı ters çevirsek bu
sefer olay gidiyor ama araç kaydedilemeyebiliyor. İki farklı sisteme "atomik" yazamazsın.

### Çözüm

1. Olayı **aynı veritabanına, aynı transaction'da** `outbox_messages` tablosuna yaz. Ya ikisi birden kaydedilir ya hiçbiri.
2. Arka plandaki `OutboxDispatcher` her saniye bekleyen satırları alır, RabbitMQ'ya gönderir, `processed_at`'i doldurur.

`GcsDbContext.SaveChangesAsync` override'ı bunu otomatik yapıyor: takip edilen her aggregate'in olaylarını toplayıp
outbox satırına çeviriyor. Handler'ların bundan haberi bile yok.

### İnce detaylar

- **`FOR UPDATE SKIP LOCKED`**: İleride 3 API kopyası olursa üçü de dispatcher çalıştırır. Bu SQL ifadesi sayesinde
  her biri **farklı** satırları alır. Aynı olay iki kez gönderilmez, kimse birbirini beklemez.
- **Publisher confirms**: RabbitMQ "aldım, diske yazdım" demeden satırı işlenmiş saymıyoruz.
- **Sıra korunur**: Bir olay gönderilemezse döngü durur (`break`). Sonraki olaylar onu geçmez, "Retired" olayı
  "Registered"dan önce gitmez.
- **At-least-once**: Nadir durumlarda (gönderdik, tam işaretlerken veritabanı koptu) aynı olay iki kez gidebilir.
  Bu yüzden her mesajın `MessageId`'si var; tüketiciler aynı id'yi ikinci kez görünce yok sayacak (**idempotent consumer**).
- **RabbitMQ çökerse?** Araç kaydı yine başarılı olur. Olaylar outbox'ta bekler, broker gelince gönderilir.
  ADR-005'teki "broker arızası uçuşu durdurmamalı" ilkesinin gerçek hali bu.

Routing key'ler: `VehicleRegistered` → `vehicle.registered`. RabbitMQ'da bir kuyruk `vehicle.*` ile bağlanırsa
tüm araç olaylarını alır.

---

## 5. API: HTTP'nin doğru kullanımı

Dosya: [VehicleEndpoints.cs](../../src/Gcs.Api/Endpoints/VehicleEndpoints.cs)

| İstek | Başarı | Olası hatalar |
|---|---|---|
| `GET /api/v1/vehicles?page=1&pageSize=20&status=Active&search=uav` | 200 + sayfa | 400 geçersiz parametre |
| `GET /api/v1/vehicles/{id}` | 200 + `ETag: "3"` | 404 |
| `POST /api/v1/vehicles` | **201 Created** + `Location` + `ETag` | 400, 409 |
| `PUT /api/v1/vehicles/{id}` + `If-Match: "3"` | 200 + yeni ETag | 400, 404, 409, **412**, **428** |
| `DELETE /api/v1/vehicles/{id}` | **204 No Content** | 404, 412 |

### ETag ve If-Match

```
GET  /api/v1/vehicles/abc      → ETag: "3"
PUT  /api/v1/vehicles/abc      If-Match: "3"   → 200, ETag: "4"
PUT  /api/v1/vehicles/abc      If-Match: "3"   → 412 Precondition Failed (artık 4)
PUT  /api/v1/vehicles/abc      (If-Match yok)  → 428 Precondition Required
```

**Bir düzeltme:** Önceki mesajımda "ikinci kaydeden 409 alır" demiştim. HTTP standardına (RFC 9110) göre doğru kod
**412**: "Gönderdiğin ön koşul (If-Match) artık doğru değil." 409 "durum çakışması" için kalıyor: callsign
kullanımda, araç emekli.

### DELETE neden idempotent?

Operatör "sil"e bastı, ağ zaman aşımına uğradı, uygulama tekrar denedi. İkinci istek `404` değil `204` döner.
Sonuç aynı: araç emekli. **Idempotent** = aynı isteği N kez göndermek 1 kez göndermekle aynı etkiyi yapar.

### Arama ve SQL injection / wildcard

`?search=%` gönderilirse ne olur? LIKE'ta `%` "her şey" demek. Kullanıcının yazdığı `%` ve `_` karakterlerini
escape ediyoruz, böylece gerçekten `%` karakterini arıyor. Değerler EF Core'da parametre olarak gittiği için SQL
injection zaten mümkün değil.

---

## 6. Testler

| Proje | Sayı | Neyi kanıtlıyor? |
|---|---|---|
| UnitTests | 99 | Value object kuralları, Vehicle davranışı, durum makinesi tablosu, handler'lar (sahte repository ile), ETag ayrıştırma, routing key |
| ArchitectureTests | 11 | Katman kuralları hâlâ sağlam (Messaging, Persistence'ı bilmiyor; Desktop, Domain'i bilmiyor) |
| IntegrationTests | 21 | Gerçek PostgreSQL + RabbitMQ: CRUD, 400/404/409/412/428, 8 eşzamanlı istek yarışı, arama, sayfalama, outbox → RabbitMQ |

### "Flaky" test avı: Gerçek bir hikâye

Testleri ilk çalıştırdığımda hepsi geçti. İkinci tam çalıştırmada **bir test** düştü, üçüncüde yine geçti.
Böyle aralıklı düşen teste **flaky** denir. En kötüsü "aman bir daha çalıştırayım" demektir, çünkü genelde gerçek
bir hatayı saklar.

Testi 6 kez arka arkaya çalıştırıp yakaladım: `Registered_vehicle_can_be_read_back_with_its_etag`.
POST cevabındaki `createdAt` ile GET cevabındaki farklıydı:

```
POST cevabı:   2026-10-07T01:08:54.1234567Z   (.NET: 100 nanosaniye hassasiyet)
GET cevabı:    2026-10-07T01:08:54.123456Z    (PostgreSQL: mikrosaniye hassasiyet)
```

Saatin son hanesi tesadüfen 0 olunca test geçiyordu (~%10), değilse düşüyordu. Yani **API gerçekten tutarsızdı**.
Masaüstü uygulaması "değişti mi?" diye zaman damgası karşılaştırsaydı hayalet değişiklikler görecekti.

Çözüm: zamanı kaynağında, veritabanının sakladığı hassasiyete yuvarlamak
([ClockExtensions.cs](../../src/Gcs.Application/Common/ClockExtensions.cs)) ve bunu deterministik bir unit testle
sabitlemek. Sonra 6 tam çalıştırmanın 6'sı da geçti.

**Ders:** Flaky test görmezden gelinmez, sebebi bulunur.

---

## 7. Kendin dene

API'yi çalıştır (`docker compose up -d --wait postgres rabbitmq`, sonra `dotnet run --project src/Gcs.Api`):

```powershell
# 1) Araç kaydet
curl.exe -i -X POST http://localhost:5134/api/v1/vehicles -H "Content-Type: application/json" `
  -d '{\"callsign\":\"uav-01\",\"mavlinkSystemId\":1,\"autopilot\":\"Px4\",\"type\":\"Multirotor\",\"connection\":{\"transport\":\"Udp\",\"host\":\"127.0.0.1\",\"port\":14550}}'
# → 201, Location ve ETag: "1" başlıklarına bak. Callsign "UAV-01" olmuş mu?

# 2) Aynısını tekrar gönder → 409, "code": "vehicle.callsign.in_use"

# 3) Hatalı veri gönder (callsign "x", port 0) → 400, tüm hatalar bir arada

# 4) RabbitMQ'yu izle: http://localhost:15672 (gcs / gcs_dev_password)
#    Exchanges → gcs.events. Bir kuyruk oluşturup "vehicle.*" ile bağla, yeni araç kaydet, mesajı gör.

# 5) Eşzamanlılık: aynı ETag ile iki PUT gönder → ikincisi 412
```

PostgreSQL'e bakmak istersen:

```powershell
docker compose exec postgres psql -U gcs -d gcs -c "select callsign, status, version from gcs.vehicles;"
docker compose exec postgres psql -U gcs -d gcs -c "select type, processed_at from gcs.outbox_messages;"
```
