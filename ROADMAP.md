# Pegline yol haritası

Hedef: ClipBoardLine'daki tüm kullanıcıya görünen özelliklerin Windows-native karşılığı.
`[x]` tamam · `[~]` kısmen · `[ ]` yapılacak

## Faz 1 — Temel ve askılar
- [x] .NET 10 çözümü, Core / App / Tests ayrımı, CI
- [x] SQLite Library deposu (işlem + geri alma), içerik adresli medya deposu
- [x] Pano dinleyici: metin / link / görsel (PNG, DIB), gizli ve geçmiş dışı kopyalar, kendi kopyaları; Gezgin'den kopyalanan görsel dosyaları, görselin yanında metin olan kopyalar (tarayıcı), alfası boş 32 bit DIB'ler
- [x] Metin kimliği ile tekrar önleme
- [x] Tür başına saklama süresi, saklanan kayıtların korunması, süresi dolanı Çöp'e taşıma
- [x] Askı gezinme durum makinesi (13 davranış testi)
- [x] Kısayol haritası: yeniden atama, devre dışı bırakma, varsayılan takma adlar
- [x] Askı penceresi: kayan panel, kavisli ipler, mandallar, numaralı kartlar, sayfalama, tema / kart boyutu altyapısı
- [x] Kopyala (`Enter` kapatır, `Ctrl+C` açık tutar), `Ctrl+Enter` önceki uygulamaya yapıştır, sabitle, askıdan indir + geri al (32), Çöp'e taşı
- [x] Askı araması (`Shift+S`), satır göster/gizle, sadece sabitlenenler, seçim modu
- [x] Genel kısayollar, tepsi, tek örnek, hoş geldin bildirimi
- [x] Türkçe + İngilizce arayüz (diğer 7 dil Faz 9'da eklendi)

## Faz 2 — Önizleme ve Eylemler
- [x] Space ile çerçevesiz önizleme (görsel / metin / link), oklarla askılar arası yerinde gezinme
- [x] Görsel zoom (%100–400, imleç sabit), sürükleyerek kaydırma, çift tıkla sıfırlama
- [x] Metin düzenleme (`E`, otomatik kayıt, `Ctrl+Enter` bitir), link tarayıcıda aç (`Ctrl+O`)
- [x] Eylemler paleti (`Ctrl+K`): bağlama duyarlı, Türkçe/aksansız aranabilir, kısayolları ve devre dışı nedenini gösterir; henüz yapılmamış eylemler "Yakında" olarak görünür
- [x] Paletten: kütüphaneye kaydet, sol/sağdakileri askıdan kaldır, dosyayı aç / Gezgin'de göster, son görseli kopyala, kaydedilmemişleri temizle, klasöre ekle/çıkar, etiket kaldır

## Faz 3 — Kütüphane penceresi
- [x] Izgara / liste (sanallaştırılmış, 20 000 kayıt), kenar çubuğu (Kütüphanem, Favoriler, Düzenlenmemiş, Son kopyalar, Çöp), klasörler (bir alt seviye), etiketler, sayılar
- [x] Otomatik kaydeden ayrıntı paneli (ad, içerik, not), klasör/etiket çıkarma, not oluşturma (`Ctrl+N`), klasör oluştur / yeniden adlandır / kaldır, etiket yeniden adlandır / kaldır
- [x] Tür filtreleri (`Ctrl+0..3`), sıralama (yeni/eski/başlık/son kopyalanan/en iyi eşleşme), tarih filtresi, aksan duyarsız arama, klavye davranış testleri
- [x] Çöp: `S`/`Delete` geri alınabilir taşır, `Backspace`/`Ctrl+Z` geri alır, Çöp görünümünde tuşlar asla kalıcı silmez; geri getir, kalıcı sil, Çöp'ü boşalt, kaydedilmemişleri temizle onayı
- [x] Kütüphanede önizleme (`Space`), Eylemler paleti (kapsam, görünüm, sıralama, klasör komutları), sağ tık menüsü; askılardan `L`, tepsi ve genel kısayol ile açılır
- [x] Alan adı ve çoklu etiket filtresi arayüzü (Faz 7 filtre paneli)

## Faz 4 — Düzenleme ve aktarım
- [x] Klasöre sabitle (`Shift+P`, son kullanılanlar, `Ctrl+N`, klasörsüz sabitle), Adlandır ve sabitle (`Alt+P`, toplu sıralı, arada silineni atlar), etiket ekle / oluştur (`Ctrl+Shift+T`); kardeş klasör adları benzersiz
- [x] Dışa aktar (`Ctrl+Shift+E`, ad çakışmasında " (2)", üzerine yazmaz, başarısızları sayar), sürükle-bırak (görsel dosya; `Alt` ile metin/link .txt/.url dosyası; seçimin tamamı), kütüphanede kenar çubuğu klasörüne sürükleme
- [x] Yapıştırma hedefi doğrulaması (UI Automation: parola alanı / değişmiş hedef reddi; yalnızca kimlik ve yetenek okunur)
- [ ] Canlı kabul: sürükle-bırak hedefleri (Gezgin, tarayıcı, Office) ve gerçek uygulamalarda yapıştırma doğrulaması elle denenecek

## Faz 5 — Görsel işaretleme ve kırpma
- [x] Kalem, fosforlu kalem, dikdörtgen, elips, ok, çizgi, metin (yerinde yazma, çift tıkla düzenleme); seç/taşı/boyutlandır; 8 renk, 4 kalınlık, dolgu, 4 yazı boyutu; `Shift` ile düz çizgi / kare
- [x] Orijinali bozmayan kırpma (tutamaçlar, taşıma, sıfırlama; `C` doğrudan kırpma), 60 adım geri al / yinele, `Ctrl+S`, `Ctrl+Enter`/`Esc` kaydet ve bitir, işaretler sonradan düzenlenebilir vektör olarak saklanır, tam çözünürlükte PNG
- [x] Önizlemede `Ctrl+Z` / `Ctrl+Shift+Z` ile oturum içi düzenleme geçmişi; yakınlaştırma (%100–400) ve `Space`+sürükle kaydırma
- [ ] Canlı kabul: fare ve klavyeyle uçtan uca işaretleme elle denenecek

## Faz 6 — Yakalama kaynakları ve OCR
- [x] Ekran görüntüsü yakalama (`Ctrl+Alt+A`, tepsi): imleçteki ekran, tam çözünürlük, doğrudan kırpma ile açılır
- [x] Windows Ekran görüntüleri klasörü (taşınmış olsa da) ve en fazla 3 izlenen klasör (tepsiden ekle / kaldır); panoya da düşen ekran görüntüsü tek kayıt olur
- [x] Yerel OCR (Windows.Media.Ocr; Türkçe yoksa İngilizce / profil dilleri), arka plan kuyruğu, düzenlenen görsel yeniden okunur, görseldeki yazıyı kopyala (`Ctrl+Shift+C`), OCR metni askı ve Kütüphane aramasında
- [x] Yeni görselde askıları göster (tam ekran uygulama / oyun önündeyken asla)
- [ ] Canlı kabul: `Ctrl+Alt+A` ekran yakalama elle denenecek

## Faz 7 — Görsel arama
- [x] MobileCLIP-S2 ONNX (Microsoft.ML.OnnxRuntime, CPU): sabitlenmiş dışa aktarım (dosya başına boyut + SHA-256), CLIP BPE tokenizer, referansla aynı letterbox + karo politikası ve eşleşme eşikleri; doğrulama: kırmızı daire / mavi kare sıralaması, kosinüsler referansla ±0.01
- [x] Arka plan dizinleme: tek çekirdek, düşük öncelik, tam ekran uygulama önündeyken bekler, boşta model bellekten çıkar; görsel başına ~80 ms; düzenlenen / silinen görselin vektörü atılır, eski sonuç asla gösterilmez
- [x] Kütüphanede birleşik / yalnızca metin / yalnızca görsel arama (kapsam içinde sıralama, "En iyi eşleşme"de metin eşleşmeleri önce), filtre paneli: tarih, alan adı, etiketler, sabitlenenler; tür ve klasör mevcut çiplerden / kenar çubuğundan
- [ ] Canlı kabul: gerçek kütüphanede görsel arama elle denenecek

## Faz 8 — Linkler ve kayıtlar
- [x] Link zenginleştirme: başlık, site, yazar, küçük resim (Open Graph / `<title>`, YouTube oEmbed); yalnızca herkese açık HTTPS, her çözülen adres ve yönlendirme yeniden denetlenir (DNS rebinding yok), boyut / süre sınırı, jeton / imza / kimlik bilgisi içeren linkler hiç istenmez; en fazla 2 eşzamanlı istek, başarısızlar 1 saat sonra yeniden; "Link önizlemesini yenile"; tepsiden kapatılabilir
- [x] MP4/MOV kayıtları: Gezgin'den kopyala, Kütüphaneye sürükle-bırak, Ekran Alıntısı Aracı'nın "Screen Recordings" klasörü ve izlenen klasörler (yazma bitene kadar beklenir); kapak karesi ve süre; önizlemede oynatma (`K`, sarma çubuğu)
- [x] Videoyu kes (Windows.Media.Editing): orijinal hiç değişmez, kesilen parça yeni dosya; `I`/`O` başlangıç/bitiş, "Videonun tamamını kullan" ile geri dönüş; kopyala / sürükle / dışa aktar kesilmiş videoyu adıyla verir
- [x] Kütüphanem boşken "Son kopyaları göster" düğmesi
- [ ] Canlı kabul: gerçek ekran kaydıyla oynatma ve kesme penceresi elle denenecek

## Faz 9 — Ayarlar ve kişiselleştirme
- [x] Ayarlar penceresi (`Ctrl+,`, tepsi, Eylemler paleti): Genel, Geçmiş, Kaynaklar, Klavye kısayolları, Güncellemeler, Hakkında; her denetim tepsiyle aynı ayar yolunu kullanır, pencereyi açmak hiçbir şey yazmaz
- [x] Kısayol kaydedici: genel kısayollar kayıt sırasında bırakılır, Ctrl/Alt/Win zorunlu, birbirleriyle ve Windows'taki başka uygulamalarla çakışma uyarısı; gezinme kısayolları aynı yerde (askı / Kütüphane / önizleme / işaretleme / metin düzenleme) çakışamaz; kapat, varsayılana dön, tümünü sıfırla; Kütüphane için `Ctrl+Alt+L` hazır düğmesi
- [x] Yumuşak renkler / nötr cam ve kart boyutu askılara anında uygulanır; görünen askılar (en az biri açık); sesler (asılma ve indirme için kod içinde üretilen kısa sesler); oturum açılışında başlat (kullanıcı Run kaydı, Windows'tan okunur, yalnızca kurulu sürümde)
- [x] Askıları indirme: imleç ekranın üst kenarında 350 ms bekleyince (varsayılan kapalı, düğme basılıyken ya da üstte görev çubuğu varken asla), yeni bir şey asılınca 2,5 sn göz atma (klavyeyi almaz, imleç üzerindeyken açık kalır, tıklayınca normal askıya döner); tam ekran uygulamaların üzerinde yalnızca izin verilirse
- [x] 9 dil (en, tr, es, fr, pt-BR, az, zh-Hans, ja, ko) ve sistem dili (pt → pt-BR, zh-CN/SG → zh-Hans); 466 metnin hepsi her dilde, yer tutucular denetlendi; dil değişince "Pegline'ı yeniden aç"
- [ ] Canlı kabul: Ayarlar penceresi, kısayol kaydedici ve üst kenardan açılma elle denenecek

## Faz 10 — Dağıtım
- [x] Kurulum paketi (Velopack): `PeglineApp-win-Setup.exe`, taşınabilir zip, tam ve delta güncelleme paketleri; yönetici izni yok; kurulum klasörü (`PeglineApp`) kütüphane klasöründen (`Pegline`) ayrı, kaldırma kütüphaneye dokunmaz ve oturum açılış kaydını siler
- [x] GitHub Releases'tan güncelleme: açılışta ve saatte bir kontrol (kapatılabilir), bildirim ve tepsi menüsü, indirme ve kurulum yalnızca kullanıcı seçince, kurulumdan önce açık düzenlemeler kaydedilir; gizli depo için DPAPI ile şifrelenmiş GitHub jetonu; Eylemler paletinde "Güncellemeleri denetle" / "Güncellemeyi kur"
- [x] `v*` etiketiyle sürüm iş akışı (test, model hazırlama, delta, yayın); yerel `scripts/pack.ps1`
- [x] Logo: uygulama / kurulum ikonu (koyu yuvarlak karo), şeffaf tepsi simgesi, Ayarlar ve kurulum ekranında logo
- [ ] Kod imzalama sertifikası (şimdilik imzasız; SmartScreen uyarısı)
- [~] Canlı kabul: kurulum ve kaldırma denendi (0.1.0); 0.1.0 → 0.1.1 güncellemesi denenecek

## 0.1.1 — İlk deneme turundan düzeltmeler
- [x] Askıdaki yazılar keskin: gölge kartın içeriğinden ayrı çizilir, metin ve link kartları dik asılır (yalnızca görseller eğik)
- [x] Kartlar ekran genişliğine göre sığar (9 kart sınırı kalktı), kart boyutuna uyar ve ipe eşit aralıkla yayılır; satırlar aynı sütunları paylaşır
- [x] Yeniden gösterilen satır yerleşim bittikten sonra pencereyi bir kez daha boyutlar (yarım kalan satır)
- [x] `Ctrl`+tık ile ardışık olmayan ve farklı türden (görsel + link) kartlar birlikte seçilir
- [x] Metin / link kartları `Ctrl` basılıyken sürüklenince Gezgin'e .txt / .url dosyası olarak iner (Gezgin `Alt`'ı "kısayol oluştur" sayıp reddediyordu)
- [x] İşaretleme stil paneli: kalınlık, dolgu ve yazı boyutu tek panelde, açık kalır (eski menü odak değişiminde hemen kapanıyordu)
- [x] Görseldeki yazıyı kopyala, metni Metinler askısına da asar
- [x] Kütüphane filtre paneli açıkken araç çubuğu yenilense de yerinde kalır; seçili tarih / seçenek çipi vurgu rengiyle görünür
- [x] "Yeni bir şey kopyalanınca": her kopyada insin (varsayılan) / yalnızca görsellerde / inmesin; eski açık-kapalı ayarı bir kez taşınır
- [x] Askıların sağ üstünde Kütüphane ve Ayarlar düğmeleri; Ayarlar'daki logo yüksek kaliteli ölçeklenir; Güncellemeler metinleri Türkçeleşti

## 0.1.2 — Lisans
- [x] Pegline © 2026 Enes Sarı, tüm hakları saklı (`LICENSE`, Hakkında, dosya özellikleri)
- [x] Başka projeden kopyalanan test dosyaları kaldırıldı; aynı davranışlar kendi testleriyle (askı, Kütüphane klavyesi, kopyalama kuralları, geçmiş temizleme) denetleniyor
- [x] Üçüncü taraf bildirimleri yalnızca uygulamada gerçekten kullanılan bileşenleri listeler
