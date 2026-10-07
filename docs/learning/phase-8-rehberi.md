# Phase 8 Rehberi: Güvenlik (giriş, roller, yetki, rate limiting)

Phase 7'de araca komut gönderebiliyorduk, ama API'ye ulaşabilen **herkes** her şeyi yapabiliyordu. Audit log'a da
istemcinin `X-Operator` başlığına yazdığı isim giriyordu: "ali" yazan herkes Ali olabiliyordu. Gerçek bir İHA'ya komut
veren bir sistemde bu kabul edilemez. Phase 8 üç soruya cevap veriyor:

1. **Kimsin?** → Kimlik doğrulama (authentication): kullanıcı adı + şifre → token.
2. **Ne yapabilirsin?** → Yetkilendirme (authorization): rol → izin → endpoint.
3. **Ne kadar sık?** → Rate limiting: şifre deneyen ya da API'yi boğan biri yavaşlatılır.

![Giriş penceresi](../images/gcs-desktop-phase8-login.png)

Bu görüntü gerçek: masaüstü uygulaması artık bir giriş penceresiyle açılıyor. Giriş yaptıktan sonra sağ üstte
"mustafa (Operator)" ve **Sign out** butonu görünüyor.

---

## 1. Authentication ile authorization arasındaki fark

İki kelime birbirine çok benzer ama bambaşka şeyler anlatır:

| | Soru | Başarısız olursa |
|---|---|---|
| **Authentication** | "Sen gerçekten Ali misin?" | `401 Unauthorized`: kim olduğunu bilmiyorum |
| **Authorization** | "Ali, sen bunu yapabilir misin?" | `403 Forbidden`: kim olduğunu biliyorum ama izin yok |

Örnek: Observer rolündeki Ayşe giriş yaptı ve bir araca TAKEOFF göndermeye çalıştı. Cevap 401 değil **403**. Sistem
onun Ayşe olduğunu biliyor, ama Ayşe'nin rolü komut veremez.

## 2. Şifreler: neden "hash", neden yavaş?

Veritabanında şifrenin kendisini **asla** saklamayız. Saklasaydık, sızan bir veritabanı yedeği bütün şifreleri
verirdi. Ve insanlar aynı şifreyi başka yerlerde de kullanır.

Onun yerine **hash** saklıyoruz: tek yönlü bir fonksiyon. `hash("benim-şifrem")` hesaplanabilir, ama hash'ten
şifreye geri dönülemez. Girişte kullanıcının yazdığı şifreyi hash'leyip kayıttakiyle karşılaştırırız.

Neden SHA-256 değil de PBKDF2 kullanıyoruz? SHA-256 **çok hızlıdır**. Saldırgan saniyede milyarlarca tahmin
deneyebilir. PBKDF2 aynı hesabı 100 000 kez tekrarlar, bilerek yavaştır:

```
Bizim için:       1 giriş   × ~50 ms   → kimse fark etmez
Saldırgan için:   1 milyar tahmin × 50 ms → ~1,5 yıl
```

Bir de **salt** (tuz) var: her şifreye rastgele bir ek. İki kullanıcının şifresi aynı olsa bile hash'leri farklı
çıkar. Saldırgan da hazır tablolarla (rainbow table) bütün kullanıcılara birden saldıramaz.

Bunların hiçbirini kendimiz yazmadık. ASP.NET Core Identity'nin `PasswordHasher`'ını kullanıyoruz. **Kendi
kriptonu yazma** kuralı bu işin altın kuralıdır.

```csharp
internal sealed class IdentityPasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<User> _hasher = new();
    public string Hash(string password) => _hasher.HashPassword(null!, password);
    ...
}
```

Şifre kuralı sadece "12–128 karakter". "Bir büyük harf, bir rakam, bir sembol" gibi kurallar yok. Uzun bir cümle
(`ankara-kalesi-sabah-sisli`), `P@ssw0rd!`'den çok daha zor tahmin edilir ve akılda kalır.

## 3. Hesap kilitleme ve "aynı cevap" kuralı

Arka arkaya **5 yanlış şifre**, hesabı **5 dakika** kilitler. Böylece saldırgan dakikada en fazla birkaç tahmin
yapabilir.

Bir incelik daha var. Şu iki cevaba bak:

```
Kullanıcı yok:    401 "Username or password is wrong."
Şifre yanlış:     401 "Username or password is wrong."
```

İkisi **bilerek** aynı. "Böyle bir kullanıcı yok" deseydik, saldırgan önce hangi kullanıcı adlarının var olduğunu
bulur, sonra sadece onlara saldırırdı. Buna **kullanıcı adı sızdırma** (user enumeration) denir.

Sadece mesaj değil, **süre** de aynı olmalı. Kullanıcı yoksa hash hesaplamadan hemen cevap verseydik, cevap 1 ms'de,
şifre yanlışsa 50 ms'de gelirdi. Süreyi ölçen biri farkı görürdü. Bu yüzden bilinmeyen kullanıcı için de sahte bir
hash doğruluyoruz:

```csharp
if (user is null)
{
    hasher.Verify(_dummyHash ??= hasher.Hash("not-a-real-password-just-for-timing"), password);
    return UserErrors.InvalidCredentials;
}
```

## 4. Token'lar: access ve refresh

Giriş başarılı olunca iki şey dönüyor:

| | Access token | Refresh token |
|---|---|---|
| Ne | JWT (imzalı JSON) | 32 rastgele bayt |
| Süre | **15 dakika** | **12 saat** (bir vardiya) |
| Nerede kullanılır | Her istekte: `Authorization: Bearer ...` | Sadece `/auth/refresh` ile yeni token almak için |
| Sunucu kontrolü | İmza + süre, **veritabanına bakmadan** | Veritabanında (hash olarak) |
| İptal edilebilir mi | Hayır (süresi dolana kadar geçerli) | Evet |

### JWT nedir?

Üç parçalı, noktalarla ayrılmış bir metin:

```
eyJhbGciOiJIUzI1NiJ9 . eyJzdWIiOiIuLi4iLCJuYW1lIjoibXVzdGFmYSIsInJvbGUiOiJPcGVyYXRvciJ9 . imza
     başlık                          içerik (payload)                                     imza
```

İçerik base64 ile kodlanmıştır, **şifreli değildir**. Herkes okuyabilir:

```json
{ "sub": "01a1...", "name": "mustafa", "role": "Operator", "exp": 1791403200 }
```

Güvenlik **imzadan** gelir. Sunucu içeriği gizli bir anahtarla (HMAC-SHA256) imzalar. Biri `"role": "Administrator"`
yazıp kendini terfi ettirmeye kalkarsa imza artık tutmaz ve istek `401` alır. `AuthTests` tam olarak bunu deniyor:
imzası bozulmuş bir token ile ünlü `alg: none` (imzasız) saldırısının ikisi de reddediliyor.

> JWT'nin içine asla gizli bilgi koyma. Base64 şifreleme değildir.

### Neden iki token?

Tek bir uzun ömürlü token olsaydı ne olurdu? Çalınırsa saldırgan onu günlerce kullanabilirdi, çünkü JWT'yi
veritabanına bakmadan doğruluyoruz, yani "iptal" diye bir şey yok.

Çözüm: **kısa** access token (15 dk) ve **iptal edilebilir** refresh token. Çalınan bir access token en fazla 15 dakika
işe yarar. Refresh token ise her kullanımda veritabanında kontrol edilir.

### Rotation ve reuse detection

Her refresh işleminde eski refresh token iptal edilir ve **yenisi** verilir. Yani her refresh token **tek kullanımlıktır**.

Bunun güzel bir yan etkisi var:

```
1. Ali giriş yaptı               → refresh = R1
2. Saldırgan R1'i kopyaladı
3. Ali'nin uygulaması yeniledi   → R1 iptal, refresh = R2
4. Saldırgan R1'i kullanmaya çalıştı → R1 zaten kullanılmış!
   → Bir kopya var demektir → Ali'nin BÜTÜN token'ları iptal (R2 dahil)
5. Ali'nin uygulaması giriş ekranına döner, Ali tekrar girer. Saldırgan dışarıda kalır.
```

Buna **reuse detection** denir. Kod:

```csharp
if (stored.IsRevoked)   // daha önce kullanılmış bir token geldi
{
    await refreshTokens.RevokeAllAsync(stored.UserId, now, cancellationToken);
    LogReuseDetected(logger, stored.UserId.Value);
    return UserErrors.InvalidRefreshToken;
}
```

Refresh token'ları veritabanında da düz metin olarak değil, **SHA-256 hash** olarak saklıyoruz. Burada hızlı hash
yeterli, çünkü token 32 rastgele bayttır ve tahmin edilemez. Şifrelerdeki yavaş hash ihtiyacı burada yok.

### Bilinen sınır

Bir yönetici Ali'nin rolünü değiştirdi ya da hesabını kapattı. Ali'nin elindeki access token 15 dakika daha geçerli.
Refresh token'ları hemen iptal edildiği için 15 dakika sonra dışarıda kalır. Anında iptal gerekirse kullanıcı başına bir
"security stamp" eklenebilir ([ADR-015](../adr/ADR-015-authentication-and-authorization.md)). Bu bilinçli bir takas:
her istekte veritabanına bakmamak karşılığında 15 dakikalık bir gecikmeyi kabul ediyoruz.

## 5. Roller → izinler → endpoint'ler

Dört rol var:

| Rol | Kim | Ne yapar |
|---|---|---|
| Observer | Amir, ziyaretçi | Sadece izler |
| Operator | Pilot | Bağlanır, görev planlar, komut verir |
| Maintenance | Bakım ekibi | Araç kaydeder/düzenler, kontrol için bağlanır, **komut veremez** |
| Administrator | Sistem yöneticisi | Her şey + kullanıcı yönetimi |

Önemli tasarım kararı: **endpoint'ler rol adı söylemez, izin adı söyler.**

```csharp
// Kötü: rol adı endpoint'e gömülü
group.MapPost("/{id}/commands", ...).RequireAuthorization(policy => policy.RequireRole("Operator", "Administrator"));

// İyi: endpoint sadece ne gerektiğini söyler
group.MapPost("/{id}/commands", ...).RequireAuthorization(Permissions.Command);
```

Hangi rolün hangi izne sahip olduğu **tek bir tabloda** duruyor (`Permissions.cs`):

```csharp
[Read]           = [Observer, Operator, Maintenance, Administrator],
[LinkVehicles]   = [Operator, Maintenance, Administrator],
[PlanMissions]   = [Operator, Administrator],
[Command]        = [Operator, Administrator],
[ManageVehicles] = [Maintenance, Administrator],
[ManageUsers]    = [Administrator],
```

Yarın "Maintenance de görev planlayabilsin" denirse tek bir satır değişir, hiçbir endpoint'e dokunulmaz.

### Varsayılan olarak kapalı (secure by default)

```csharp
.SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
```

Bu satır şunu söylüyor: "Policy'si olmayan her endpoint giriş ister." Yarın biri yeni bir endpoint ekleyip yetki
vermeyi unutursa, o endpoint **herkese açık değil, kapalı** olur. Unutmanın sonucu güvenlik açığı değil, en kötü
ihtimalle "çalışmıyor" şikayetidir. Anonim kalan uçlar bilinçli olarak işaretlendi: health probe'ları, `system/info`,
login/refresh/logout.

### Yetki matrisi testi

Bir tablo yazmak kolay, doğru uygulandığını göstermek zor. `AuthorizationMatrixTests` her endpoint'i **her rolle**
çağırıp kontrol ediyor:

```
POST /api/v1/vehicles/{id}/commands
   Observer      → 403 ✓
   Operator      → 404 (geçti; araç yok ama yetki var) ✓
   Maintenance   → 403 ✓
   Administrator → 404 ✓
   anonim        → 401 ✓
```

28 endpoint × 4 rol + anonim = 140 kontrol. Testler rastgele id'ler ve boş gövdeler kullanıyor, bu yüzden yetkili
istekler 404 ya da 400 alıyor ve hiçbir şey değişmiyor. Bizim için önemli olan 401/403 olup olmadığı.

### Audit log artık sahte isim kabul etmiyor

Komut endpoint'i operatör adını artık token'dan alıyor:

```csharp
var result = await handler.HandleAsync(id, user.Identity?.Name, request, cancellationToken);
```

`AuthTests` içinde bir test, `X-Operator: someone-else` başlığı göndermeyi deniyor. Audit'te yine gerçek kullanıcı adı
(`audit-pilot`) görünüyor.

## 6. Rate limiting

| Kural | Sınır | Kime göre |
|---|---|---|
| Genel | 600/dk | giriş yapmışsa kullanıcıya, yapmamışsa IP'ye |
| Giriş (login/refresh) | 10/dk | IP'ye |
| Komutlar | 60/dk | kullanıcıya |

Sınır aşılınca `429 Too Many Requests` ve `Retry-After: 37` ("37 saniye sonra tekrar dene") döner.

Neden giriş için ayrı ve sıkı bir sınır var? Hesap kilitleme bir hesabı korur. Saldırgan ise 1000 farklı kullanıcı
adına birer şifre deneyebilir (**password spraying**). IP başına dakikada 10 deneme, bunu da yavaşlatır.

Health probe'ları ve SignalR muaf. Docker'ın sağlık kontrolü rate limit'e takılıp API'yi "hasta" sanmamalı.

Sıralama da önemli:

```csharp
app.UseAuthentication();  // önce kim olduğunu bul
app.UseRateLimiter();     // sonra kullanıcıya göre say
app.UseAuthorization();   // en son izni kontrol et
```

Rate limiter authentication'dan önce gelseydi herkes "anonim" sayılır, aynı IP'deki bütün operatörler tek bir kotayı
paylaşırdı.

## 7. Sırlar (secrets) repoya girmez

JWT imza anahtarını bilen biri, istediği rolle token üretebilir. Bu anahtar sistemin en değerli sırrıdır. Kurallar:

| Ortam | Anahtar nereden gelir |
|---|---|
| `dotnet run` (Development) | `dotnet user-secrets` (Windows profilinde, repo dışında). Yoksa her çalışmada rastgele üretilir ve uyarı yazılır |
| Docker Compose | `.env` dosyası (`.gitignore`'da) → `Jwt__SigningKey` ortam değişkeni |
| CI | Her çalıştırmada `openssl rand` ile yeni üretilir, loglarda maskelenir |
| Testler | Her test koşusunda rastgele (`TestSecrets`) |

`appsettings.json`'da `Jwt:SigningKey` **yok**. Anahtar yoksa veya 32 bayttan kısaysa API **başlamayı reddeder**.
Sessizce zayıf bir anahtarla çalışmak, hiç çalışmamaktan kötüdür.

İlk yönetici (bootstrap administrator) de böyle oluşturuluyor. Kullanıcı tablosu **boşsa** ve şifre ayarlıysa bir
`admin` oluşturulur. Tabloda tek bir kullanıcı bile varsa bu adım hiçbir şey yapmaz. Yani yapılandırmadaki şifre
mevcut bir hesabı asla sıfırlayamaz.

Senin bilgisayarında bunları ben ayarladım:

* `.env` dosyasında `JWT_SIGNING_KEY` ve `GCS_ADMIN_PASSWORD` var. Dosya git'e girmiyor.
* `dotnet user-secrets` ile aynı değerler `dotnet run` için de ayarlandı.
* Docker stack'inde `admin` (Administrator), `mustafa` (Operator) ve `gozlemci` (Observer) kullanıcıları var.
  Şifreleri `.env`'deki `GCS_ADMIN_PASSWORD` ile aynı. İlk iş: giriş yap ve bu şifreleri değiştir (`POST /auth/password`).

## 8. Masaüstü tarafı

* **Giriş penceresi** → başarılı olunca ana pencere açılıyor.
* Token'lar **sadece bellekte** tutuluyor, diske yazılmıyor. Uygulamayı kapatınca oturum biter. Laptop çalınsa bile
  diskte kopyalanacak bir token yoktur.
* `AuthenticatingHandler` her isteğe `Bearer` başlığını ekliyor. Access token'ın bitmesine 1 dakika kala sessizce
  yeniliyor. İki istek aynı anda yenilemeye kalkarsa sadece biri yeniliyor (`SemaphoreSlim`). Tek kullanımlık refresh
  token'ı iki kez harcamak, kendi oturumunu reuse detection ile kapatmak olurdu.
* Sunucu `401` dönerse oturum biter ve uygulama "Your session ended. Sign in again." mesajıyla giriş ekranına döner.
* SignalR bağlantıları da token ile açılıyor (`AccessTokenProvider`).
* Rolün komut izni yoksa (Observer, Maintenance), kontrol paneli görünüyor ama "Take control" kapalı. Asıl kontrol yine
  sunucuda. Arayüz sadece kullanıcıyı boşuna denemekten kurtarıyor.

![Giriş yapılmış ana ekran](../images/gcs-desktop-phase8-signed-in.png)

## 9. Testler

```bash
dotnet test --project tests/Gcs.UnitTests            # kullanıcı kuralları, kilitleme, token yenileme, giriş ekranı
dotnet test --project tests/Gcs.IntegrationTests     # yetki matrisi, oturumlar, rate limit (Docker gerekir)
```

Öne çıkanlar:

* **Yetki matrisi:** her endpoint × her rol + anonim.
* **Rotation/reuse:** eski refresh token'ı tekrar kullanınca yeni token da ölüyor.
* **Kilitleme:** 5 yanlış şifreden sonra doğru şifre de reddediliyor.
* **Sahte token:** imzası değiştirilmiş token ve `alg: none` reddediliyor.
* **Rate limit:** 4. giriş denemesi `429` + `Retry-After` alıyor. Komut kotası kullanıcı başına ayrı.
* **Masaüstü:** `FakeTimeProvider` ile saati 14,5 dakika ileri alıyoruz. İki paralel istek tek bir refresh yapıyor.

Phase 7'nin bütün testleri (49 entegrasyon testi) de artık token ile çalışıyor. `TestUsers` sınıfı, ihtiyaç olan
kullanıcıyı gerçek `/users` API'si üzerinden oluşturup giriş yapıyor.

## 10. Kendin dene

```bash
# Token olmadan: 401
curl -i localhost:8080/api/v1/vehicles

# Giriş (şifre .env'deki GCS_ADMIN_PASSWORD)
curl -s -X POST localhost:8080/api/v1/auth/login -H "Content-Type: application/json" \
     -d '{"username":"gozlemci","password":"..."}'
# → accessToken'ı kopyala

# Observer okuyabilir: 200
curl -H "Authorization: Bearer <token>" localhost:8080/api/v1/vehicles

# Observer kontrol alamaz: 403
curl -i -X POST -H "Authorization: Bearer <token>" localhost:8080/api/v1/vehicles/<id>/command-lease

# Token'ın içine bak (şifreli değil!): https://jwt.io ya da
echo "<token>" | cut -d. -f2 | base64 -d
```

Masaüstünde: `dotnet run --project src/Gcs.Desktop` ile aç, `gozlemci` ile gir. Kontrol panelinde "Take control"
kapalı. Çıkış yapıp `mustafa` ile gir: artık açık.

## 11. Kendine sorular

1. 401 ile 403'ün farkı ne? Observer TAKEOFF gönderirse hangisini alır?
2. Şifreleri neden SHA-256 ile değil de PBKDF2 ile hash'liyoruz? Refresh token'lar için SHA-256 neden yeterli?
3. "Kullanıcı bulunamadı" mesajı vermek neden kötü bir fikir?
4. JWT'nin içeriğini herkes okuyabiliyorsa, biri rolünü neden değiştiremiyor?
5. Bir refresh token iki kez kullanılırsa sistem neden kullanıcının *bütün* oturumlarını kapatıyor?
6. Fallback policy olmasaydı, yetki eklemeyi unutan bir geliştirici ne tür bir açık yaratırdı?
7. `UseRateLimiter`'ı `UseAuthentication`'dan önce koysaydık ne değişirdi?

<details>
<summary>Kısa cevaplar</summary>

1. 401: kim olduğun bilinmiyor. 403: biliniyor ama izin yok. Observer 403 alır.
2. SHA-256 çok hızlıdır, kısa ve tahmin edilebilir şifreler saniyede milyarlarca kez denenebilir. PBKDF2 bilerek
   yavaştır. Refresh token 32 rastgele bayttır, tahmin edilemez, o yüzden hızlı hash yeterli.
3. Saldırgan önce geçerli kullanıcı adlarını bulur, sonra saldırıyı onlara yoğunlaştırır.
4. İçerik imzalıdır. Değiştirilen içerik imzayla tutmaz, imzayı da gizli anahtar olmadan yeniden üretemez.
5. Tek kullanımlık bir token'ın ikinci kez gelmesi, bir kopyası olduğunu gösterir. Hangisinin gerçek kullanıcı
   olduğunu bilemeyiz, ikisini de dışarı atarız. Gerçek kullanıcı tekrar giriş yapar.
6. O endpoint herkese, hatta giriş yapmamış kişilere açık olurdu.
7. Limiter kullanıcıyı tanımaz, herkesi IP'ye göre sayardı. Aynı ağdaki bütün operatörler tek kotayı paylaşırdı.

</details>
