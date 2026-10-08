# Genel Bakış: UAV Yer Kontrol İstasyonu, 12 fazda

Bu belge projenin tamamını tek yerde özetliyor: ne yaptık, hangi sırayla ve neden. Her fazın ayrıntılı, örnekli
anlatımı kendi rehberinde (`phase-N-rehberi.md`), kararların gerekçesi ADR'lerde ([docs/adr](../adr/README.md)).

## Proje tek cümlede

Birden fazla insansız hava aracını MAVLink üzerinden izleyen ve yöneten, savunma sanayii ürünü gibi tasarlanmış bir yer
kontrol istasyonu (GCS). Öncelik sırası: **güvenilirlik → emniyet → güvenlik → izlenebilirlik → özellik**.

```
 Avalonia masaüstü ──REST + SignalR (HTTPS)──► Nginx ──► Gcs.Api ──► Application ──► Domain
                                                            │
                                     Infrastructure: PostgreSQL · RabbitMQ · MAVLink · OpenTelemetry
                                                            │
                                     MAVLink UDP/TCP ──► simülatör · PX4 SITL · gerçek araç
```

## Fazlar

| Faz | Ne eklendi? | En önemli ders |
|---|---|---|
| **0 Analiz** | Teknoloji seçimi, mimari, riskler | Kod yazmadan önce "neden" sorusunu cevaplamak |
| **1 Temel** | Çözüm yapısı, Clean Architecture katmanları, CI, Docker Compose | Katman kurallarını **mimari testlerle** zorlamak (kurala uymayan kod derlenir ama test kırılır) |
| **2 Araç alanı** | `Vehicle` aggregate, value object'ler, EF Core + PostgreSQL, CRUD API, ETag ile eşzamanlılık | Geçersiz durumu **oluşturulamaz** yapmak (ör. portsuz UDP bağlantısı) |
| **3 MAVLink** | Kendi MAVLink 2 kodlayıcımız (pymavlink'e karşı golden test), UDP/TCP, simülatör, heartbeat, yeniden bağlanma | "Bağlı" bilgisi taşıma katmanından değil **heartbeat'ten** gelir; geri çekilme (backoff) + jitter |
| **4 Telemetri** | Son durum, SignalR ile 5 Hz canlı yayın, geçmişin PostgreSQL'e örneklenmesi, outbox → RabbitMQ | **Outbox deseni**: veritabanı ve mesaj kuyruğu arasında olay kaybetmemek |
| **5 Masaüstü** | Avalonia MVVM istemci: araç listesi, harita, telemetri panelleri | UI'ı arka uçtan ayırmak; view model'leri UI olmadan test etmek |
| **6 Görev planlama** | Haritada görev, doğrulama, MAVLink görev protokolüyle yükleme/indirme | "Kaydedilebilir" ile "uçurulabilir"i ayırmak (taslak eksik olabilir) |
| **7 Komutlar** | ARM, TAKEOFF, RTL..., komut kirası (bir araca bir operatör), ACK zaman aşımı ve yeniden deneme, değiştirilemez denetim kaydı | Kritik komut çift tıklamayla iki kez gitmemeli; her deneme kayda geçmeli |
| **8 Güvenlik** | JWT + dönen refresh token, roller → izinler, hesap kilitleme, rate limit, güvenlik başlıkları | **Varsayılan olarak kapalı** (secure by default) ve her uç nokta × her rol testi |
| **9 İzlenebilirlik** | OpenTelemetry trace/metrik/log, trace bağlamı outbox ve RabbitMQ'dan geçiyor, sağlık detayları, Aspire dashboard | Bir operatör tıklamasını HTTP'den araca kadar tek bir izde görmek |
| **10 PX4 SITL** | Gerçek PX4 otopilotu Docker'da, kalkış → görev → RTL uçuş testi, CI'da ayrı iş | Kendi simülatörün kendi hatanı göremez. Gerçek PX4 ilk denemede 3 hata buldu |
| **11 Kurulum** | Tek komutla Ubuntu 24.04 kurulumu: Nginx (TLS, WebSocket, rate limit), UFW, Let's Encrypt / kendi CA, yedekleme | Docker yayınladığı portlarda UFW'yi atlar; proxy arkasında gerçek istemci IP'si |
| **12 Gelişmiş ağ** | Bağlantı kalitesi (kayıp, gecikme, telsiz RSSI, not), aynı portta çok araç, topoloji API'si, MANET soyutlaması | Ölçümü tek bir nota çevirmek; GCS mesh'i yönetmez, gözlemler |

## Sayılarla

| | |
|---|---|
| Test | 379 unit · 11 mimari · 132 entegrasyon (biri gerçek PX4 uçuruyor) |
| CI işleri | Build and test · Docker smoke test · PX4 SITL flight · Deployment config |
| ADR | 19 karar kaydı |
| Rehber | 12 faz rehberi + bu genel bakış |

## Nasıl çalıştırılır?

**Geliştirme (kendi bilgisayarında):**

```bash
cp .env.example .env                                  # JWT_SIGNING_KEY ve GCS_ADMIN_PASSWORD'u doldur
docker compose up -d --build --wait                   # API, veritabanı, kuyruk, dashboard, simülatör
docker compose --profile sitl up -d --build --wait    # + gerçek PX4 (isteğe bağlı)
dotnet run --project src/Gcs.Desktop                  # masaüstü istemci
```

**Testler:**

```bash
dotnet test --project tests/Gcs.UnitTests
dotnet test --project tests/Gcs.ArchitectureTests
dotnet test --project tests/Gcs.IntegrationTests                                        # Docker gerekir
GCS_PX4_SITL=1 dotnet test --project tests/Gcs.IntegrationTests -- --filter-trait "Category=Sitl"   # gerçek PX4 uçuşu
```

**Sunucu (Ubuntu Server 24.04):**

```bash
sudo git clone https://github.com/mustafaklee/uav-ground-control-station.git /opt/gcs
sudo /opt/gcs/deploy/install.sh --domain gcs.ornek.com --tls letsencrypt --email ops@ornek.com
# internetsiz ağ: --tls internal (kendi CA'mız)
```

Ayrıntılar: [deployment.md](../deployment.md).

## Bilinen eksikler

* **Seri port yok.** USB telemetri telsizleri için şimdilik bir köprü (mavlink-router) gerekiyor.
* **MAVLink imzasız ve şifresiz.** Porta ulaşan herkes MAVLink gönderebilir.
* **Tek API örneği.** Bağlantılar, komut kiraları ve rate limit sayaçları bellekte duruyor.
* **Access token anında iptal edilemiyor.** En geç 15 dakikada kendiliğinden düşüyor.
* **Harita çevrimiçi.** Sahadan önce çevrimdışı harita karoları gerekiyor.
* **Bağlantı kalitesi eşikleri** saha verisiyle ayarlanmadı. SNMP ve NetFlow entegre değil.
* **Let's Encrypt yolu** uçtan uca denenmedi. Bunun için gerçek ve herkese açık bir alan adı gerekiyor.

Hepsi README'deki "Known limitations" tablosunda, ilgili ADR'lerle birlikte duruyor.

## Bu projeden çıkan genel dersler

1. **Test edilmeyen varsayım, hatadır.** Gerçek PX4 üç hata buldu. Temiz Ubuntu container'ı, proxy'ye fazla geniş
   güvendiğimizi gösterdi. CI, gözle görünmeyen bir YAML hatası buldu. Hiçbiri sadece kodu okuyarak bulunamazdı.
2. **Soyutlama, ihtiyaç anında kendini ödüyor.** Phase 3'teki `IMavlinkTransport` sayesinde Phase 12'de port
   paylaşımı, bağlantı koduna dokunmadan eklendi.
3. **Kararı ve gerekçesini yaz.** "Neden Redis yok?", "Neden SSH açık?", "Neden SNMP sonra?" sorularının cevabı ADR'lerde.
4. **Güveni dar tut.** Proxy'ye güvenirken bütün ağa değil, tek bir adrese güvenmek gibi.
