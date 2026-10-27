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
- [ ] Space ile çerçevesiz önizleme (görsel / metin / link), oklarla yerinde gezinme
- [ ] Görsel zoom (%100–400), tekerlek / sürükleme ile kaydırma, çift tıkla sıfırlama
- [ ] Metin düzenleme (`E`), link tarayıcıda aç (`Ctrl+O`)
- [ ] Eylemler paleti (`Ctrl+K`): bağlama duyarlı, aranabilir, kısayolları gösterir

## Faz 3 — Kütüphane penceresi
- [ ] Izgara / liste, kenar çubuğu (Kütüphanem, Son kopyalar, Sabitlenenler, Düzenlenmemiş, Çöp), klasörler, etiketler
- [ ] Otomatik kaydeden düzenleme paneli (ad, not, içerik), not oluşturma
- [ ] Tür filtreleri (`Ctrl+0..3`), sıralama, arama, klavye sözleşmesi (library-keyboard)
- [ ] Çöp: geri getir, kalıcı sil, `Ctrl+Z`, geçmişi temizle onayı

## Faz 4 — Düzenleme ve aktarım
- [ ] Klasöre sabitle (`Shift+P`, son kullanılanlar, `Ctrl+N`), Adlandır ve sabitle (`Alt+P`, toplu sıralı), etiket ekle (`Ctrl+Shift+T`)
- [ ] Dışa aktar (`Ctrl+Shift+E`), sürükle-bırak (görsel dosya; `Alt` ile metin/link dosyası), kütüphanede klasöre sürükleme
- [ ] Yapıştırma hedefi doğrulaması (UI Automation: parola alanı / değişmiş hedef reddi)

## Faz 5 — Görsel işaretleme ve kırpma
- [ ] Kalem, fosforlu kalem, dikdörtgen, elips, ok, çizgi, metin; seç/taşı/boyutlandır; renk / kalınlık / dolgu
- [ ] Orijinali bozmayan kırpma, geri al / yinele, `Ctrl+S`, düzenlenebilir işaretlerin kalıcılığı

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
