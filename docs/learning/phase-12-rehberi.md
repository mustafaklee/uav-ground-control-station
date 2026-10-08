# Phase 12 Rehberi: Gelişmiş ağ (bağlantı kalitesi, çoklu araç, topoloji)

Şimdiye kadar GCS'nin bağlantı hakkında söyleyebildiği tek şey şuydu: **"bağlı"** ya da **"bağlı değil"**. Bu, bir
telefonda sadece "şebeke var / yok" görmek gibi. Ama gerçek hayatta şu sorular daha önemli:

* Şebeke var, ama **kaç çubuk?** (sinyal gücü)
* Mesajım karşıya **ne kadar sürede** gidip dönüyor? (gecikme)
* Mesajların **kaçta kaçı yolda kayboluyor?** (paket kaybı)
* Aynı ağda **başka kim var?** (topoloji)

Phase 12 bu soruları cevaplıyor. Spesifikasyon bu fazı "değerlendirilecek konular" olarak listeliyordu (MANET, SNMP,
NetFlow...). Hepsini yapmak yerine işe yarayan bir çekirdek seçtim ve gerisini nedenleriyle erteledim. Kararlar
[ADR-019](../adr/ADR-019-advanced-networking.md)'da.

---

## 1. Paket kaybı: "toplam" yetmez, "son 10 saniye" lazım

Phase 3'ten beri paket kaybını MAVLink'in **sıra numaralarından** hesaplıyorduk. Her araç gönderdiği her mesaja
0'dan 255'e kadar artan bir numara koyar. Arada boşluk varsa mesaj kaybolmuştur:

```
gelen sıra numaraları:  10, 11, 12, 15, 16
                                  ↑ 13 ve 14 hiç gelmedi → 2 kayıp
```

Sorun şuydu: bu sayı **bağlantı başladığından beri toplamdı**. Bir saatlik temiz uçuştan sonra son bir dakika çok kötü
geçse bile oran neredeyse kıpırdamaz:

```
1 saat temiz:   36 000 mesaj, 0 kayıp
son 1 dakika:      600 mesaj, 300 kayıp   (yarısı kayıp! operatör bunu görmeli)
toplam oran:    300 / 36 900 = %0.8       ← "her şey yolunda" der. Yanlış.
```

Çözüm **kayan pencere** (sliding window): son 10 saniyeyi 1 saniyelik 10 kovada tutuyoruz.

```
saniye:   ... | t-3 | t-2 | t-1 |  t  |      ← 10 kovalık halka
gelen:         48    50    12    9
kayıp:          0     0    38    3          son 10 sn kayıp = Σkayıp / (Σgelen + Σkayıp)
```

Her yeni saniye, 10 saniye önceki kovanın üzerine yazılıyor (halka, yani ring buffer). Kötü bir an birkaç saniye
içinde görünüyor ve 10 saniye sonra kendiliğinden pencereden çıkıyor. Kod: `src/Gcs.Mavlink/Connections/LinkQualityMonitor.cs`.

Test etmek için gerçek zamanı beklemiyoruz. `FakeTimeProvider` ile saati elle ileri alıyoruz: "60 saniye temiz, sonra
2 kötü saniye" senaryosu milisaniyede koşuyor (`LinkQualityMonitorTests`).

## 2. Gecikme: TIMESYNC ile gidiş-dönüş süresi

Gecikmeyi ölçmenin en kolay yolu `ping` gibi: bir mesaj gönder, cevabı bekle, aradaki süreye bak. MAVLink'te bunun
hazır bir mesajı var: **TIMESYNC (#111)**.

```
GCS → araç:   TIMESYNC(tc1 = 0, ts1 = 1 000 000 000)      "benim saatim şu an 1 000 000 000 ns"
araç → GCS:   TIMESYNC(tc1 = 52 000, ts1 = 1 000 000 000) "senin saatini aynen geri gönderiyorum"
GCS:          şimdi = 1 018 000 000 → gidiş-dönüş = 18 ms
```

En güzel yanı: **iki saatin aynı olması gerekmiyor.** Sadece kendi saatimizi kendi saatimizle karşılaştırıyoruz. Aracın
saati 1970'te de olsa fark etmez.

Bir mesajı her saniye heartbeat ile birlikte gönderiyoruz. PX4 ve ArduPilot TIMESYNC'e ayarsız cevap veriyor. Bunu
gerçek PX4 ile doğruladım: SITL uçuş testi artık gidiş-dönüşün ölçüldüğünü ve bağlantının "Good" olduğunu kontrol ediyor.

**Ölçüm gürültülüdür.** Bir cevap bir kez 500 ms gecikebilir. Bunu yumuşatmak için **EWMA** (üssel ağırlıklı hareketli
ortalama) kullanıyoruz:

```
yeni ortalama = eski + 0.3 × (yeni ölçüm − eski)

100 ms, 100 ms, sonra bir kez 500 ms:
   100 + 0.3 × (500 − 100) = 220 ms     (500'e zıplamıyor, ama yükselişi de gizlemiyor)
```

α = 0.3 seçtim: birkaç saniyede gerçek değere oturuyor, tek bir yavaş cevaba kapılmıyor.

**Küçük bir hata ve dersi:** İlk sürümde zamanı `TimeProvider.GetTimestamp()` ile nanosaniyeye çeviriyordum. Testte
sahte saatin zaman damgası "1. yıldan beri geçen 100 ns'lik adım" sayısıydı (≈ 6.4 × 10¹⁷). Nanosaniyeye çevirince
`long`'un sınırını (9.2 × 10¹⁸) aştı ve sonuç anlamsızlaştı. Çözüm: zamanı **monitörün kendi başlangıcından** saymak.
Zaten sadece farklar önemliydi. Ders: büyük sayıları çarparken taşmayı (overflow) düşün.

## 3. Telsiz durumu: RADIO_STATUS ("modem status")

Sahada araçlar çoğunlukla bir **telemetri telsizi** ile bağlanır (ör. SiK, 433/915 MHz). Bu telsizler kendi
durumlarını **RADIO_STATUS (#109)** mesajıyla bildirir:

| Alan | Anlamı |
|---|---|
| `rssi` | yer telsizinin karşıyı ne kadar güçlü duyduğu (SiK: 0–255, yaklaşık 2 dB/adım) |
| `remrssi` | hava telsizinin bizi ne kadar güçlü duyduğu |
| `noise`, `remnoise` | iki uçtaki gürültü seviyesi |
| `rxerrors`, `fixed` | bozuk gelen ve hata düzeltmeyle kurtarılan paketler |
| `txbuf` | telsizin gönderme tamponunun doluluğu (%) |

**RSSI − gürültü = sinyal payı.** Araç uzaklaştıkça RSSI düşer, gürültü aynı kalır, pay daralır. Payın kapanması
bağlantının kopmak üzere olduğunun en erken işaretidir.

Bir tuzak vardı: telsiz, mesajı **aracın** sistem kimliğiyle değil **kendi** kimliğiyle gönderir (SiK: 51). Bağlantı
kodumuz "sadece kendi aracımın mesajlarını al" diye 51'i çöpe atıyordu. RADIO_STATUS'u bu yüzden kimden gelirse gelsin
kabul ediyoruz.

Simülatörümüz artık bir SiK telsizini de taklit edebiliyor (`SimulateRadio`). Sinyal evden her 10 m'de 1 adım
düşüyor. Geliştirme compose'undaki simülatörde bu açık, yani masaüstünde RSSI hemen görünüyor.

## 4. Hepsini tek kelimeye çevirmek: Good / Fair / Poor / Lost

Operatör uçuş sırasında beş sayıyı okuyamaz. Bir bakışta anlayacağı bir şey lazım. Domain katmanında saf bir kural
(`LinkQualityRules`) var:

| Not | Kural (ilk tutan kazanır) |
|---|---|
| **Lost** | bağlı değil ya da 3 sn'dir hiç mesaj yok |
| **Poor** | son 10 sn kaybı ≥ %15 ya da gidiş-dönüş ≥ 1000 ms |
| **Fair** | son 10 sn kaybı ≥ %3 ya da gidiş-dönüş ≥ 300 ms |
| **Good** | geri kalan her şey |

Neden domain'de, saf bir fonksiyon? Çünkü **iş kuralı**. Veritabanı, ağ, SignalR... hiçbirini bilmesi gerekmiyor.
Saf olduğu için test etmek çok kolay: 4 giriş, 1 çıkış (`LinkQualityRulesTests`). Eşikler tek bir yerde duruyor; saha
verisiyle ayar yapmak tek satırlık bir değişiklik.

"Lost" için neden 3 sn sessizlik ayrıca var? Heartbeat bekçisi (watchdog) bağlantıyı "Reconnecting"e çekmeden önce de
operatör bir sorun olduğunu görmeli.

Masaüstünde her aracın altında renkli bir satır var:

```
● UAV-01
  Px4 Multirotor · sysid 1 · Udp
  Connected
  Good · 12 ms · loss 0.4 % · 41 msg/s · RSSI 182/176      ← yeşil (Fair: sarı, Poor: turuncu-kırmızı)
```

Sunucu bu bilgiyi 2 saniyede bir `LinkQualityUpdated` olarak gönderiyor. Durum değişiklikleri (Connected →
Reconnecting) ise eskisi gibi **anında** gidiyor. Kalite sürekli değişen bir şey olduğu için örnekleniyor (sampling).
Durum ise nadir ve önemli olduğu için olay (event) olarak gidiyor.

## 5. Aynı portta birden fazla araç

Bu fazın en büyük değişikliği. Eskiden her araç kendi UDP portunu **tek başına** açıyordu:

```
UAV-1 → 14550   ✓
UAV-2 → 14550   ✗ "port kullanımda"   (ikinci araç aynı portu açamaz)
```

Ama gerçekte çoğu sistem **herkesi aynı porta** gönderir: PX4'ün çok araçlı SITL'i, sürü (swarm) senaryoları,
birden fazla aracı tek bir ağ geçidinden geçiren telsizler. Çözüm: **portu paylaşmak** ve gelen paketi içindeki
**sistem kimliğine** göre dağıtmak.

```
UAV-1 (sysid 1) ─┐                          ┌─► UAV-1'in bağlantısı
UAV-2 (sysid 2) ─┼─► 0.0.0.0:14550 ─ hub ───┼─► UAV-2'nin bağlantısı
sysid 7 (yeni)  ─┘                          └─► kimse: "duyuldu ama kayıtlı değil"
```

Sistem kimliği MAVLink başlığında sabit bir yerde durur. Paketin tamamını çözmeye gerek yok, bir bayta bakmak yeter:

```csharp
public static byte? PeekSystemId(ReadOnlySpan<byte> data) => data switch
{
    [0xFD, _, _, _, _, var systemId, ..] => systemId,   // MAVLink 2: 6. bayt
    [0xFE, _, _, var systemId, ..]       => systemId,   // MAVLink 1: 4. bayt
    _ => null,
};
```

(Bu C#'ın **list pattern** özelliği: dizinin şeklini bir desenle eşleştiriyor.)

Tasarımın önemli noktaları (`UdpEndpointHub`):

* **Referans sayımı:** Soket ilk araç bağlanınca açılıyor, son araç ayrılınca kapanıyor. Unit testi bunu gerçekten
  kontrol ediyor: son bağlantı kapanınca aynı portu başka bir soket açabilmeli.
* **Aynı kimlik iki kez olamaz:** İki araç aynı portta aynı sistem kimliğiyle kayıtlıysa paketleri ayırt edilemez.
  İkinci bağlantı açık bir hata mesajıyla reddediliyor. Sessizce birbirinin trafiğini çalmalarından iyidir.
* **Sınırlı kuyruk:** Her aracın gelen kutusu en fazla 1024 paket tutuyor. Dolarsa **en eskisi** atılıyor
  (`DropOldest`). Bir bağlantı takılırsa bellek sınırsızca büyümüyor. Telemetride eski veri zaten yeni veriden değersiz.
* **Telsiz kuralı:** Kayıtlı olmayan bir kimlik, bir aracın **adresinden** geliyorsa (aynı IP ve port), o aracın
  telsizidir. Paket o araca gidiyor.
* **Yarış durumu (race condition):** Bir araç bağlanırken, aynı anda son araç ayrılıp soketi kapatıyor olabilir. Bunu
  `TryJoin` ile çözdüm: kapanmakta olan bir sokete katılmak `null` döndürüyor, çağıran da yeni bir soket açıyor.

`MavlinkConnection` bu değişiklikten **hiç etkilenmedi**. O hâlâ sıradan bir `IMavlinkTransport` görüyor. Phase 3'te
taşıma katmanını soyutlamanın faydası burada ortaya çıktı.

## 6. Topoloji: "ağda kim var?"

`GET /api/v1/network/topology` küçük bir graf döndürüyor:

```
GCS ── udp://0.0.0.0:14550 ──┬── UAV-1 (sysid 1)   Good, 12 ms, %0
       (telsiz: RSSI 180)    ├── UAV-2 (sysid 2)   Fair, 340 ms, %4
                             └── sysid 7: duyuldu, kayıtlı değil
GCS ── tcp://10.0.0.5:5760 ───── UAV-3 (sysid 3)   ...
```

"Kayıtlı değil" satırı pratikte çok işe yarar. Sahada biri bir aracı açmış ama GCS'ye eklemeyi unutmuşsa, araç burada
görünür.

## 7. MANET soyutlaması: GCS mesh'i yönetmez, gözlemler

**MANET** (Mobile Ad-hoc NETwork): her düğümün hem uç hem yönlendirici olduğu, merkezi altyapısı olmayan, hareketli bir
ağ. Sürü İHA'larda araçlar birbirinin mesajını taşıyabilir: UAV-3 GCS'yi göremiyorsa paketini UAV-2 üzerinden iletir.

İlk akla gelen "GCS mesh yönlendirmesini yapsın" fikri. Yapmadım, çünkü bu işi **telsizler zaten yapıyor**. Mesh
telsizleri yönlendirmeyi kendi içlerinde çözer. GCS'nin bunu tekrar yapması hem gereksiz hem de yeni arıza noktaları
eklerdi. GCS'nin işi **gözlemlemek**: hangi düğüm var, kimi duyuyor, sinyal ne durumda.

Bunun için bir arayüz tanımladım:

```csharp
public interface IRadioNetworkProvider
{
    IReadOnlyList<RadioNode> GetRadioNodes();   // düğümler, durumları ve komşuları
}
```

* **Bugün:** tek bir uygulaması var. MAVLink'teki RADIO_STATUS mesajlarından noktadan noktaya telsiz çiftlerini
  çıkarıyor.
* **Yarın:** bir mesh telsiz seçildiğinde ikinci bir uygulama yazılır. O uygulama telsize kendi yönetim arayüzünden
  (SNMP ya da REST) sorar, düğümleri ve komşu listelerini döndürür. Topoloji uç noktası bütün sağlayıcıları birleştirir,
  başka hiçbir kod değişmez.

Buna **port/adapter** (hexagonal) mimari deniyor. Uygulama katmanı "telsiz bilgisi lazım" der, nereden geldiğini
bilmez. Phase 3'teki `IMavlinkTransport` ile aynı fikir.

## 8. Ertelenenler ve nedenleri

| Konu | Neden şimdi değil? |
|---|---|
| SNMP | Hangi telsiz modelinin hangi MIB'ini (yönetim veri şeması) okuyacağımızı bilmeden yazılacak kod tahmin olur. Arayüz hazır |
| NetFlow / IPFIX | Akış verisini yönlendirici ve switch'ler üretir. Bunu toplamak ayrı bir ağ izleme sisteminin işi, GCS'nin değil |
| GCS'nin mesh yönlendirmesi | Telsizler zaten yapıyor (bölüm 7) |
| MAVLink 2 imzalama, MAVLink'i sadece araç ağından kabul etmek | Araçlara anahtar dağıtımı gerekiyor; ayrı bir güvenlik işi |
| Seri port (USB telsiz) | Henüz seri taşıma yok. İlk USB telsizle birlikte gelecek |

"Yapmamaya karar vermek" de bir mühendislik kararı. Önemli olan nedenini yazmak, ki altı ay sonra biri "bunu neden
yapmamışız?" diye sorduğunda cevap hazır olsun.

## 9. İzleme: aynı sayılar dashboard'da da

Bağlantı kalitesi OpenTelemetry gauge'ları olarak da yayınlanıyor (her biri araç kimliğiyle etiketli):

| Gauge | Birim |
|---|---|
| `gcs.link.rtt` | ms |
| `gcs.link.packet_loss` | 0–1 (son 10 sn) |
| `gcs.link.message_rate` | mesaj/sn |
| `gcs.link.radio.rssi` | telsiz birimi |

Phase 9'daki Aspire dashboard'da grafik olarak görünüyorlar. Herhangi bir OTLP arka ucunda alarm kurulabilir: "bir
aracın kaybı 1 dakika boyunca %10'un üstündeyse haber ver" gibi.

## 10. Nasıl test ettim?

| Test | Ne kanıtlıyor? |
|---|---|
| `LinkQualityRulesTests` | eşikler, sınır değerler dahil (%2.9 Good, %3 Fair) |
| `LinkQualityMonitorTests` | kayan pencere, EWMA, sahte ve eski TIMESYNC cevaplarının yok sayılması |
| `MavlinkConnectionTests` | GCS gerçekten TIMESYNC gönderiyor, cevap gidiş-dönüşü veriyor; telsiz mesajı başka kimlikten de kabul ediliyor |
| `GoldenFrameTests` | yeni iki mesaj pymavlink'in ürettiği baytlarla birebir aynı |
| `UdpEndpointHubTests` | gerçek UDP soketleriyle: doğru dağıtım, yinelenen kimlik reddi, "duyuldu" listesi, telsiz kuralı, soketin bırakılması |
| `MultiVehicleNetworkTests` | uçtan uca: aynı porta 3 simülatör (biri kayıtsız, biri telsizli), iki bağlantı, ölçümler ve topoloji |
| `Px4SitlFlightTests` | **gerçek PX4** TIMESYNC'e cevap veriyor, bağlantı Good |

Toplam: 379 unit, 11 mimari, 131 entegrasyon testi yeşil.

## 11. Özet

| Kavram | Bir cümlede |
|---|---|
| Kayan pencere | Son N saniyeyi kovalarda tutmak. Yeni sorunlar hızlı görünür, eskiler kendiliğinden çıkar |
| RTT / TIMESYNC | Kendi saatimizi gönderip geri almak. Karşı tarafın saati önemsiz |
| EWMA | Gürültülü ölçümü yumuşatan ortalama: `eski + α × (yeni − eski)` |
| RSSI ve gürültü | Sinyal gücü ve arka plan gürültüsü. Arasındaki pay bağlantının sağlığı |
| Demultiplexing | Tek kanaldan gelen karışık trafiği bir anahtara göre ayırmak (burada sistem kimliği) |
| Referans sayımı | Paylaşılan kaynağı ilk kullanıcıda açıp son kullanıcıda kapatmak |
| MANET | Merkezsiz, hareketli mesh ağ. GCS onu yönetmez, gözlemler |
| Port/adapter | Uygulama "ne lazım" der, adaptör "nereden gelir" der. Yeni kaynak = yeni adaptör |
