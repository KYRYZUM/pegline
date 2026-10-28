# Pegline yol haritası

Hedef: ClipBoardLine'daki tüm kullanıcıya görünen özelliklerin Windows-native karşılığı.
`[x]` tamam · `[~]` kısmen · `[ ]` yapılacak

## Faz 1 — Temel ve askılar
- [x] .NET 10 çözümü, Core / App / Tests ayrımı, CI
- [x] SQLite Library deposu (işlem + geri alma), içerik adresli medya deposu
- [x] Pano dinleyici: metin / link / görsel (PNG, DIB), gizli ve geçmiş dışı kopyalar, dosya kopyaları, kendi kopyaları
- [x] Metin kimliği ile tekrar önleme (sözleşme testi)
- [x] Tür başına saklama süresi, saklanan kayıtların korunması, süresi dolanı Çöp'e taşıma
- [x] Askı gezinme durum makinesi (13 paylaşılan sözleşme senaryosu)
- [x] Kısayol haritası: yeniden atama, devre dışı bırakma, varsayılan takma adlar
- [x] Askı penceresi: kayan panel, kavisli ipler, mandallar, numaralı kartlar, sayfalama, tema / kart boyutu altyapısı
- [x] Kopyala (`Enter` kapatır, `Ctrl+C` açık tutar), `Ctrl+Enter` önceki uygulamaya yapıştır, sabitle, askıdan indir + geri al (32), Çöp'e taşı
- [x] Askı araması (`Shift+S`), satır göster/gizle, sadece sabitlenenler, seçim modu
- [x] Genel kısayollar, tepsi, tek örnek, hoş geldin bildirimi
- [~] Türkçe + İngilizce arayüz (diğer 7 dil: Faz 9)

## Faz 2 — Önizleme ve Eylemler
- [x] Space ile çerçevesiz önizleme (görsel / metin / link), oklarla askılar arası yerinde gezinme
- [x] Görsel zoom (%100–400, imleç sabit), sürükleyerek kaydırma, çift tıkla sıfırlama
- [x] Metin düzenleme (`E`, otomatik kayıt, `Ctrl+Enter` bitir), link tarayıcıda aç (`Ctrl+O`)
- [x] Eylemler paleti (`Ctrl+K`): bağlama duyarlı, Türkçe/aksansız aranabilir, kısayolları ve devre dışı nedenini gösterir; henüz yapılmamış eylemler "Yakında" olarak görünür
- [x] Paletten: kütüphaneye kaydet, sol/sağdakileri askıdan kaldır, dosyayı aç / Gezgin'de göster, son görseli kopyala, kaydedilmemişleri temizle, klasöre ekle/çıkar, etiket kaldır

## Faz 3 — Kütüphane penceresi
- [x] Izgara / liste (sanallaştırılmış, 20 000 kayıt), kenar çubuğu (Kütüphanem, Favoriler, Düzenlenmemiş, Son kopyalar, Çöp), klasörler (bir alt seviye), etiketler, sayılar
- [x] Otomatik kaydeden ayrıntı paneli (ad, içerik, not), klasör/etiket çıkarma, not oluşturma (`Ctrl+N`), klasör oluştur / yeniden adlandır / kaldır, etiket yeniden adlandır / kaldır
- [x] Tür filtreleri (`Ctrl+0..3`), sıralama (yeni/eski/başlık/son kopyalanan/en iyi eşleşme), tarih filtresi, aksan duyarsız arama, klavye sözleşmesi (library-keyboard, 10 senaryo)
- [x] Çöp: `S`/`Delete` geri alınabilir taşır, `Backspace`/`Ctrl+Z` geri alır, Çöp görünümünde tuşlar asla kalıcı silmez; geri getir, kalıcı sil, Çöp'ü boşalt, kaydedilmemişleri temizle onayı
- [x] Kütüphanede önizleme (`Space`), Eylemler paleti (kapsam, görünüm, sıralama, klasör komutları), sağ tık menüsü; askılardan `L`, tepsi ve genel kısayol ile açılır
- [ ] Alan adı ve çoklu etiket filtresi arayüzü (model hazır; Faz 7 filtre paneliyle)

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
- [ ] Ekran görüntüsü yakalama (`Ctrl+Alt+A`, imleçteki ekran, kırpma ile açılır)
- [ ] Windows ekran görüntüsü klasörü ve en fazla 3 izlenen klasör
- [ ] Yerel OCR (Windows.Media.Ocr, EN/TR), görseldeki yazıyı kopyala (`Ctrl+Shift+C`), OCR'ın aramaya katılması

## Faz 7 — Görsel arama
- [ ] MobileCLIP-S2 ONNX (Microsoft.ML.OnnxRuntime), arka plan indeksleme
- [ ] Kütüphanede metin / görsel / birleşik mod ve tür, tarih, alan adı, etiket, klasör, sabitleme filtreleri

## Faz 8 — Linkler ve kayıtlar
- [ ] Link zenginleştirme: site / YouTube başlığı, yazar, küçük resim (yerel ve gizli parametreli URL'ler hariç)
- [ ] MP4/MOV kayıtları: içe aktarma, izlenen klasör, oynatma, Trim (Windows.Media.Editing), dosya olarak kopyala / sürükle / dışa aktar

## Faz 9 — Ayarlar ve kişiselleştirme
- [ ] Ayarlar penceresi: Genel, Geçmiş, Kaynaklar, Klavye kısayolları (kaydedici), Güncellemeler, Hakkında
- [ ] Yumuşak renkler / nötr cam, kart boyutu, sesler, oturum açılışında başlat, tam ekran uygulamalarda görünürlük
- [ ] 9 dil (en, tr, es, fr, pt-BR, az, zh-Hans, ja, ko) ve sistem dili

## Faz 10 — Dağıtım
- [ ] Kurulum paketi ve imzalı otomatik güncelleme (Velopack + GitHub Releases), saatlik kontrol, onaylı kurulum
- [ ] Logo / ikon (kullanıcıdan gelecek), uygulama ve kurulum ikonları
