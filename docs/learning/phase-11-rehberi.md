# Phase 11 Rehberi: Sunucuya kurulum (Deployment)

Şimdiye kadar her şey **senin bilgisayarında** çalışıyordu: `docker compose up`, `http://localhost:8080`, şifresiz
HTTP. Bu bir mutfakta yemek yapmaya benziyor. Phase 11'de restoran açıyoruz: yemek aynı, ama artık kapıda bir
güvenlik görevlisi (firewall), bir resepsiyon (Nginx), kilitli bir kasa (sırlar) ve her gece alınan bir kopya
(yedek) var.

Hedef tek bir komut:

```bash
sudo /opt/gcs/deploy/install.sh --domain gcs.lab.internal --tls internal
```

Bu komut, **boş bir Ubuntu Server 24.04** makinesini birkaç dakikada çalışan bir yer kontrol istasyonu sunucusuna
çeviriyor. Kararların İngilizce özeti [ADR-018](../adr/ADR-018-deployment.md)'de, kullanım kılavuzu
[deployment.md](../deployment.md)'de.

---

## 1. Büyük resim

```
 operatörler (Gcs.Desktop)                     araçlar / modemler
        │ HTTPS + WebSocket, 443/tcp                  │ MAVLink, 14550/udp
┌───────▼──────────────── Ubuntu Server 24.04 ────────▼──────────────────┐
│  UFW: 443/tcp, MAVLink udp ve SSH (hız sınırlı) dışında her şey kapalı │
│                                                                        │
│  nginx ──(proxy ağı)──► gcs-api:8080 ◄── udp 14550                     │
│                              │                                         │
│                 (backend ağı, dışarıya kapalı)                         │
│                 postgres · rabbitmq · otel-dashboard                   │
└────────────────────────────────────────────────────────────────────────┘
```

Dışarıdan sadece **iki kapı** açık:

| Kapı | Kim kullanıyor? | Ne geçiyor? |
|---|---|---|
| 443/tcp | Operatörler | HTTPS (REST API) ve WebSocket (SignalR canlı telemetri) |
| 14550/udp | Araçlar | MAVLink |

PostgreSQL, RabbitMQ ve API'nin kendisi (8080) dışarıdan **görünmüyor bile**.

## 2. Neden Docker Compose? Kubernetes neden değil?

Üç seçeneğe baktım:

| Seçenek | Artısı | Eksisi |
|---|---|---|
| systemd servisleri (API'yi doğrudan `dotnet` ile, Postgres'i apt ile) | Docker yok | Geliştirmede ve CI'da başka, sunucuda başka bir kurulum. "Bende çalışıyordu" sorunu |
| **Docker Compose** | **CI'daki smoke test'in kullandığı imajların aynısı. Tüm sistem tek dosyada** | Docker'ın firewall davranışı (bölüm 6) |
| Kubernetes (k3s) | Ölçekleme, sıfır kesintili güncelleme | Tek bir API için bir küme yönetmek. Öğrenmesi ve işletmesi pahalı |

Bir şeyi daha bilmek lazım: API, araç bağlantılarını **bellekte** tutuyor (her aracın UDP soketi bir süreçte). İki API
kopyası çalıştırırsak iki kopya aynı araca bağlanmaya çalışır. Yani şu an zaten tek kopya çalışmalı. Kubernetes'in en
büyük avantajı olan "10 kopya çalıştır" bize henüz bir şey kazandırmıyor. Bu konu Phase 12'de.

Geliştirme için `docker-compose.yml`, sunucu için `deploy/compose.yml` var. Farkları:

```yaml
# Geliştirme (docker-compose.yml)          # Sunucu (deploy/compose.yml)
postgres:                                   postgres:
  ports:                                      networks: [backend]   # port yok!
    - "127.0.0.1:5432:5432"
                                            networks:
                                              backend:
                                                internal: true      # dışarıya rota yok
```

`internal: true` olan bir Docker ağının internete çıkışı da yok, dışarıdan girişi de. Veritabanı sadece API ile
konuşabiliyor. Biri bir şekilde sunucuya girse bile Postgres'e ağdan ulaşamaz.

## 3. Reverse proxy: Nginx ne iş yapıyor?

**Reverse proxy**, istemci ile uygulama arasında duran bir resepsiyonist gibi. Operatör Nginx ile konuşur, Nginx de
isteği API'ye iletir. API'nin kendisi dışarıya hiç açılmaz.

Kestrel (ASP.NET Core'un web sunucusu) HTTPS'i kendisi de yapabilir. Neden araya bir şey daha koyuyoruz?

1. **TLS tek yerde.** Sertifika yenilendiğinde `nginx -s reload` yeter. API yeniden başlamaz, bağlı operatörlerin
   canlı telemetrisi kopmaz.
2. **Ucuz ilk savunma hattı.** Saniyede 1000 istek gönderen bir bot, .NET'e hiç ulaşmadan Nginx'te `429` alır.
3. **Erişim günlüğü.** Her istek için: kimden geldi, ne kadar sürdü, hangi correlation id ile.
4. **Bilinmeyen isimleri reddetmek.** Biri sunucunun IP'sine doğrudan bağlanırsa (internet tarayıcı botları bunu sürekli
   yapar), TLS el sıkışması daha başlamadan kesilir:

```nginx
server {
    listen 443 ssl default_server;
    ssl_reject_handshake on;      # bizim alan adımız dışındaki her isme: "seni tanımıyorum"
}
```

### 3.1 TLS ayarları

```nginx
ssl_protocols TLSv1.2 TLSv1.3;
add_header Strict-Transport-Security "max-age=63072000" always;
```

* **TLS 1.0 ve 1.1 kapalı.** İkisi de kırık, ve 2020'den beri hiçbir güncel tarayıcı veya .NET sürümü bunları
  kullanmıyor.
* **HSTS** (HTTP Strict Transport Security): tarayıcıya "bu siteye iki yıl boyunca sadece HTTPS ile gel" der. Biri
  araya girip seni HTTP'ye düşürmeye çalışırsa (downgrade saldırısı) tarayıcı kabul etmez.

### 3.2 WebSocket: SignalR neden ayrı bir `location` istiyor?

Canlı telemetri SignalR ile geliyor. SignalR önce normal bir HTTP isteği atar (`/hubs/telemetry/negotiate`), sonra
bağlantıyı **WebSocket**'e yükseltir (upgrade):

```
İstemci → GET /hubs/telemetry   Connection: Upgrade, Upgrade: websocket
Sunucu  ← 101 Switching Protocols
          (artık bu TCP bağlantısı iki yönlü, sürekli açık bir boru)
```

Nginx varsayılan olarak `Upgrade` başlığını **iletmez**, çünkü bu başlık "hop-by-hop"tur, yani sadece bir sonraki
durağa aittir. O yüzden açıkça iletmemiz gerekiyor:

```nginx
map $http_upgrade $connection_upgrade {   # istemci upgrade istediyse "upgrade", istemediyse boş
    default upgrade;
    ''      '';
}

location /hubs/ {
    proxy_pass http://gcs_api;
    proxy_read_timeout 1h;    # varsayılan 60 sn: sessiz bir WebSocket 60 sn sonra kesilirdi
    proxy_buffering off;      # her telemetri mesajı anında gitsin, Nginx biriktirmesin
}
```

Neden 1 saat? SignalR her 15 saniyede bir "ping" gönderir. Bağlantı canlıysa zaman aşımına hiç yaklaşmaz. 1 saat
sadece ölü bağlantıları temizler.

### 3.3 Rate limit: iki katman

| Katman | Neye göre? | Ne kadar? | Amaç |
|---|---|---|---|
| Nginx | İstemci IP adresi | Giriş: 10/dk, diğer: 20/sn | Kaba sel (flood) koruması. Ucuz |
| API (Phase 8) | Kullanıcı (giriş yaptıysa) ya da IP | Komut: 60/dk, genel: 600/dk | İnce kurallar. Kim, ne yapıyor |

Nginx kullanıcıyı tanımaz, sadece IP'yi görür. API ise token'ı açıp kullanıcıyı bilir. İki katman birbirini
tamamlıyor.

`burst` ne demek? `rate=10r/m burst=5 nodelay`: dakikada 10 istek hakkın var, ama arka arkaya 5 isteği de hemen
gönderebilirsin (mesela uygulama açılırken). 6. hızlı istek `429 Too Many Requests` alır.

## 4. En ilginç hata: Proxy arkasında herkes aynı kişi oluyor

Bu fazda yazdığım tek C# kodu bu bölüm için. Önce sorunu görelim.

Phase 8'de giriş denemelerini **IP başına** dakikada 10 ile sınırlamıştık (şifre tahmin saldırısına karşı). API, IP'yi
şuradan okuyor:

```csharp
$"ip:{http.Connection.RemoteIpAddress}"
```

Nginx'i araya koyunca ne olur? API'ye gelen **her** TCP bağlantısını Nginx açıyor. Yani API için bütün dünya tek bir
IP'den geliyor: Nginx konteynerinin adresi, `172.30.0.10`.

```
Ayşe   (203.0.113.1) ─┐
Mehmet (203.0.113.2) ─┼─► Nginx (172.30.0.10) ──► API: "3 kişi de 172.30.0.10"
Saldırgan            ─┘
```

Sonuç: Saldırgan dakikada 10 yanlış şifre denediğinde, Ayşe ve Mehmet de giriş yapamaz. Herkes **aynı kovayı**
paylaşıyor. Saldırgan rate limit'i bir DoS silahına çevirmiş olur.

**Çözüm:** Nginx, gerçek istemcinin adresini bir başlığa yazar:

```nginx
proxy_set_header X-Forwarded-For   $remote_addr;   # "bu istek aslında 203.0.113.1'den"
proxy_set_header X-Forwarded-Proto https;          # "istemci HTTPS kullandı"
```

ASP.NET Core'un `ForwardedHeadersMiddleware`'i bu başlığı okuyup `RemoteIpAddress`'i düzeltir. Ama dikkat: **bu başlığa
kim güvenebilir?** Herkes bir isteğe `X-Forwarded-For: 1.2.3.4` yazabilir. API her isteğe inansaydı, saldırgan her
denemede başka bir IP uydurur, rate limit'i tamamen atlatırdı.

Kural: **Başlığa sadece güvendiğimiz proxy'den geliyorsa inan.** Kod (`src/Gcs.Api/Http/ReverseProxy.cs`):

```csharp
forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
forwarded.ForwardLimit = 1;               // önümüzde tam bir proxy var
forwarded.KnownIPNetworks.Clear();
foreach (var network in proxy.Value.TrustedNetworks)   // "172.30.0.10/32": sadece Nginx
{
    forwarded.KnownIPNetworks.Add(IPNetwork.Parse(network));
}
```

Compose'da Nginx'e sabit bir adres verdik ve API'ye "sadece bu adrese güven" dedik:

```yaml
gcs-api:
  environment:
    ReverseProxy__Enabled: "true"
    ReverseProxy__TrustedNetworks__0: 172.30.0.10/32   # /32 = tek bir adres
nginx:
  networks:
    proxy:
      ipv4_address: 172.30.0.10
```

İlk denemede bütün ağa (`172.30.0.0/24`) güveniyordum. Kurulum testinde Nginx'in günlüğüne bakınca şunu gördüm:
sunucunun **kendi** içinden yapılan istekler `172.30.0.1`'den geliyordu. Bu, ağın geçidi (gateway), yani host
makinenin kendisi. /24'e güvenmek, sunucuda çalışan herhangi bir programın API'ye doğrudan bağlanıp sahte
`X-Forwarded-For` göndermesine izin verirdi. Ders: **güveni olabildiğince dar tut.** Bir ağa değil, tek bir adrese.

İki şey daha:

* Nginx'te `$proxy_add_x_forwarded_for` değil `$remote_addr` kullandık. İlki istemcinin gönderdiği başlığa **ekler**,
  ikincisi **üzerine yazar**. En dıştaki proxy biziz, istemcinin yazdığı hiçbir şeye güvenmiyoruz.
* Özellik varsayılan olarak **kapalı** (`Enabled: false`). Geliştirmede proxy yok. Açık olsaydı, `localhost:8080`'e
  bağlanan herkes başlıkla kimliğini değiştirebilirdi.

**Test** (`tests/Gcs.IntegrationTests/Security/ReverseProxyTests.cs`): Bellekteki test sunucusunda TCP bağlantısı yok.
O yüzden küçük bir `IStartupFilter` ile "bu istek 172.30.0.10'dan geliyor" diyoruz. Sonra iki senaryo:

1. Güvenilen proxy'den, iki farklı `X-Forwarded-For`: Birincisi 3. denemede `429` alıyor, ikincisi hâlâ serbest.
   **Ayrı kovalar.** Bu test özellik olmadan kırmızı olurdu.
2. Güvenilmeyen bir adresten (192.0.2.9), her denemede farklı `X-Forwarded-For`: Yine 3. denemede `429`. **Uydurma
   başlıklar yok sayılıyor.**

## 5. Sertifikalar: Let's Encrypt mi, kendi CA'mız mı?

HTTPS için bir **sertifika** lazım. Sertifika, "bu sunucu gerçekten gcs.example.com" diyen, güvenilir biri (CA,
Certificate Authority) tarafından imzalanmış bir belge. Kimlik kartı gibi düşün: kartı kendin basarsan kimse inanmaz,
nüfus müdürlüğü basarsa herkes inanır.

| Durum | Seçim | Neden? |
|---|---|---|
| İnternete açık sunucu, gerçek alan adı | **Let's Encrypt** | Ücretsiz, otomatik, her cihaz zaten güveniyor |
| Kapalı ağ (saha, laboratuvar, internetsiz) | **Kendi CA'mız** | Let's Encrypt sunucumuza ulaşamaz, doğrulama yapamaz |

Savunma projelerinde ikinci durum çok yaygın. Yer istasyonu bir sahada, internete hiç çıkmayan bir ağda çalışabilir.

### 5.1 Let's Encrypt ve "80 portu" sorunu

Let's Encrypt, alan adının gerçekten senin olduğunu şöyle doğrular (HTTP-01):

```
Let's Encrypt: "http://gcs.example.com/.well-known/acme-challenge/abc123 adresine şu metni koy."
certbot:       (80 portunda kısa süre dinler, metni sunar)
Let's Encrypt: (internetten o adrese gider) "Metin doğru, sertifikan hazır."
```

Ama biz sadece 443'ü açmak istiyoruz. Çözüm: certbot'un **hook**'ları. 80 portu sadece doğrulamanın sürdüğü birkaç
saniye açık:

```
pre-hook:    ufw allow 80/tcp          # aç
certbot      ... doğrulama ...
post-hook:   ufw delete allow 80/tcp   # kapat
deploy-hook: yeni sertifikayı /etc/gcs/tls'e kopyala, nginx -s reload
```

certbot paketi, günde iki kez `certbot renew` çalıştıran bir systemd timer kuruyor. Sertifika 90 günlük, son 30 güne
girince yenileniyor. Hook'lar her yenilemede kendiliğinden çalışıyor.

### 5.2 Kendi CA'mız (`deploy/tls/internal-ca.sh`)

```
UAV GCS Internal CA (10 yıl, anahtarı /etc/gcs/ca/ca.key)
        │ imzalar
        ▼
gcs.lab.internal sunucu sertifikası (397 gün, haftalık cron kontrol eder, 30 gün kala yeniler)
```

* **Neden 397 gün?** Apple, Google ve Microsoft, 397 günden uzun sunucu sertifikalarını kabul etmiyor.
* **Neden ECDSA P-256?** RSA-3072 kadar güçlü, ama anahtar çok daha küçük ve el sıkışma daha hızlı.
* **Script ikinci kez çalışınca CA'yı yeniden oluşturmaz.** Oluştursaydı, her operatör bilgisayarına CA'yı yeniden
  yüklemek gerekirdi.

Operatör bilgisayarında bir kez:

```powershell
Import-Certificate -FilePath .\ca.crt -CertStoreLocation Cert:\CurrentUser\Root
```

Bundan sonra `Gcs.Desktop --api https://gcs.lab.internal/` hatasız bağlanır. .NET'in `HttpClient`'ı Windows'un
sertifika deposunu kullanıyor.

**Uyarı:** `ca.key` artık sistemin en değerli sırrı. Onu ele geçiren, istediği isim için "güvenilir" sertifika
basabilir. Sunucu dışında, çevrimdışı bir yerde yedeklenmeli.

## 6. Firewall (UFW) ve Docker'ın sürprizi

UFW (Uncomplicated Firewall), Ubuntu'nun iptables'ı kolay kullanma aracı:

```bash
ufw default deny incoming       # gelen her şey kapalı...
ufw allow 443/tcp               # ...bunlar hariç
ufw allow 14550/udp
ufw limit 22/tcp                # SSH açık, ama 30 sn'de 6'dan fazla deneme yapan IP engellenir
ufw --force enable
```

Görev "sadece 443 ve MAVLink açık" diyordu. SSH'yi neden varsayılan olarak açık bıraktım? Uzaktaki bir sunucuda SSH'yi
kapatırsan **kendini dışarıda bırakırsın**. Kurulumu yapan kişinin bağlantısı bir sonraki komutta kopar. Bu yüzden:

* varsayılan: `--ssh limit` (açık ama hız sınırlı),
* `--ssh 10.0.0.0/24`: sadece yönetim ağından,
* `--ssh none`: tamamen kapalı (konsol erişimi olan sunucular için).

### 6.1 Docker, UFW'yi deliyor

Bu, Linux'ta Docker kullanan birçok kişinin canını yakmış bir konu. Şunu yaptığını düşün:

```yaml
postgres:
  ports:
    - "5432:5432"
```

ve UFW'de 5432 kapalı. Postgres dışarıdan erişilebilir mi? **Evet!** Docker kendi iptables kurallarını yazıyor ve
paketler UFW'nin kurallarına hiç uğramadan konteynere gidiyor.

```
gelen paket ─► iptables: DOCKER zinciri ─► konteyner        (UFW'ye hiç sorulmadı)
             └► iptables: ufw zincirleri ─► host servisleri (sshd vs.)
```

Bu yüzden asıl kuralımız: **Compose'da sadece 443 ve MAVLink portu yayınlanır** (bir de 127.0.0.1'de dashboard).
UFW ise host'un kendi servislerini (SSH gibi) koruyor. Kurulum testi yayınlanan portların listesini kontrol ediyor:
listede başka bir şey görünürse test kırmızı oluyor.

## 7. Sırlar (secrets)

Geliştirmede sırlar `.env` dosyasındaydı. Sunucuda `install.sh` onları **sunucunun üzerinde** üretiyor:

```bash
POSTGRES_PASSWORD=$(secret 32)                          # openssl rand ile rastgele
JWT_SIGNING_KEY=$(openssl rand -base64 48 | tr -d '\n')
```

ve `/etc/gcs/gcs.env` dosyasına yazıyor (izinler `0600`: sadece root okuyabilir). Sırlar git'e, CI'a veya başka bir
makineye hiç gitmiyor.

Script ikinci kez çalışınca dosya **varsa dokunmuyor**. Neden önemli?

* Postgres şifresini değiştirseydik: veritabanı ilk kurulumdaki şifreyi hatırlıyor (volume'da). API yeni şifreyle
  bağlanamazdı.
* JWT anahtarını değiştirseydik: herkesin token'ı geçersiz olur, bütün operatörler aynı anda atılırdı.

Buna **idempotent** deniyor: aynı komutu bir kez de çalıştırsan, on kez de çalıştırsan sonuç aynı. Güncelleme de bu
sayede tek komut: `git pull`, sonra aynı `install.sh`.

## 8. Yedekleme: pg_dump + cron + saklama süresi

```
03:00 her gün  cron ─► gcs-backup
                        ├─ pg_dump --format=custom  →  gcs-20261009T030000Z.dump.partial
                        ├─ pg_restore --list        →  dosya okunabiliyor mu? (doğrulama)
                        ├─ mv .partial → .dump      →  ancak şimdi "yedek" sayılıyor
                        └─ 14 günden eskileri sil, ama en yenisini asla
```

Her adımın bir sebebi var:

* **`--format=custom`:** sıkıştırılmış. Ayrıca tablo tablo geri yüklenebiliyor (sadece `missions` tablosunu geri almak
  gibi).
* **Tutarlılık:** `pg_dump` tek bir transaction snapshot'ı içinde çalışır. API yazmaya devam ederken bile yedek, tek bir
  anın tutarlı fotoğrafıdır. Yarısı 03:00:00'dan, yarısı 03:00:05'ten olan bir yedek olmaz.
* **`.partial` adı:** Disk dolarsa ya da sunucu yedeğin ortasında kapanırsa, yarım dosya "son yedek" gibi görünmesin.
* **"En yenisini asla silme":** Diyelim ki disk doldu ve yedekler 20 gün boyunca alınamadı. Basit bir `find -mtime +14
  -delete` iyi olan son yedeği de silerdi. Elimizde hiç yedek kalmazdı.

Geri yükleme:

```bash
sudo gcs-restore /var/backups/gcs/gcs-20261009T030000Z.dump
```

API'yi durduruyor (geri yükleme sırasında kimse yazmasın), `pg_restore --single-transaction` ile hepsini ya da hiçbirini
geri yüklüyor, API'yi yeniden başlatıyor.

**Önemli:** Aynı diskteki bir yedek, diskin bozulmasından kurtarmaz. Yedekler başka bir makineye kopyalanmalı (mesela
`rsync`). Bu, operatörün sorumluluğu olarak `deployment.md`'de yazıyor.

**Neden günlük dump, neden WAL arşivleme değil?** WAL arşivleme (point-in-time recovery) ile "dün 14:32:10'a dön"
diyebilirsin, ama kurulumu ve izlemesi daha karmaşık. En kötü durumda bir günlük veri kaybı, şimdilik kabul ettiğimiz
başlangıç noktası. ADR'de yazılı.

## 9. Kurulumu nasıl test ettim? Konteynerin içinde bir "sunucu"

Bir kurulum script'i yazmak kolay, **çalıştığından emin olmak** zor. Gerçek bir sanal makine kurmak yerine
`deploy/test/verify-install.sh` şunu yapıyor:

```
Windows (Docker Desktop)
└── gcs-ubuntu-test konteyneri: Ubuntu 24.04, PID 1 = systemd  ← "boş sunucu"
     └── install.sh: Docker'ı apt ile kuruyor, UFW'yi açıyor...
          └── Docker'ın içinde Docker: nginx, gcs-api, postgres, rabbitmq
```

İçteki konteyner `--privileged` çalışıyor, çünkü içinde Docker ve iptables çalıştırması gerekiyor. Gerçek sunucuda buna
gerek yok. Sonra dışarıdan kontrol ediyor:

| Kontrol | Nasıl? |
|---|---|
| HTTPS + kendi CA'mız | `curl --cacert ca.crt https://gcs.test.internal:8443/health/ready` |
| HTTP/2 ve HSTS | `curl -w '%{http_version}'`, başlıklar |
| Bilinmeyen isim reddediliyor | Başka bir isimle bağlan: TLS el sıkışması başarısız olmalı |
| Giriş Nginx üzerinden | Bootstrap admin ile `POST /api/v1/auth/login` |
| WebSocket upgrade | `negotiate`, sonra `Upgrade: websocket` → `101 Switching Protocols` |
| Rate limit | 20 hızlı yanlış giriş → en az bir `429` |
| Sadece iki port yayınlanmış | İçteki `docker ps` çıktısı |
| UFW kuralları | `ufw status` |
| Yedek, saklama süresi, geri yükleme | `gcs-backup`, 10 günlük sahte bir dosya silinmeli, `gcs-restore` sonrası API hazır |
| İdempotent | İkinci `install.sh` çalışmasından sonra `gcs.env` ve `ca.crt`'nin hash'i değişmemiş olmalı |

Let's Encrypt bu testte denenemiyor (gerçek bir alan adı ve internetten erişim gerekir). O yol için
`--letsencrypt-staging` seçeneği var: Let's Encrypt'in test sunucusu, rate limit'e takılmadan akışı denemeye yarıyor.

## 10. Özet: Bu fazda ne öğrendik?

| Kavram | Bir cümlede |
|---|---|
| Reverse proxy | İstemci ile uygulama arasında TLS, rate limit ve günlükleri üstlenen resepsiyonist |
| TLS termination | Şifreleme proxy'de çözülür, içerideki ağda API düz HTTP konuşur |
| WebSocket upgrade | HTTP bağlantısını iki yönlü, sürekli açık bir boruya çevirmek. Proxy'nin bunu açıkça iletmesi gerekir |
| X-Forwarded-For | Proxy'nin "asıl istemci bu" notu. Sadece güvenilen proxy'den gelirse inanılır |
| Let's Encrypt / HTTP-01 | Ücretsiz, otomatik sertifika. Alan adını 80 portundan doğrular |
| Kendi CA'mız | Kapalı ağlar için. CA sertifikası istemcilere bir kez yüklenir, anahtarı en değerli sır |
| UFW + Docker | Docker yayınladığı portlarda UFW'yi atlar. Asıl kural: gereksiz port yayınlama |
| `internal: true` ağ | Dışarıya rotası olmayan Docker ağı. Veritabanları burada |
| Idempotent script | Kaç kez çalışırsa çalışsın aynı sonuç. Güncelleme de aynı komut |
| pg_dump + saklama | Tutarlı, doğrulanmış, dönen yedek. Ama en yenisi hiç silinmez |

**Sıradaki faz (Phase 12, gelişmiş ağ):** birden fazla araç, bağlantı kalitesi, gecikme ve paket kaybı ekranı. Bu fazda
ertelenen iki güvenlik konusu da orada: MAVLink'i sadece araç ağından kabul etmek (`DOCKER-USER` zinciri) ve MAVLink 2
mesaj imzalama.
