# Pegline

**Kopyaladığın her şey, ipte asılı.** Windows için klavye odaklı, yerel pano geçmişi ve kütüphanesi.

Pegline; görselleri, metinleri ve linkleri ekranın üstünden kayarak inen üç ipe mandallarla asar. Son kopyaların her zaman bir tuş uzağında olur, önemli olanları yerel Kütüphanede saklarsın, hepsini klavyeyle yönetirsin.

> Native .NET 10 / WPF. Electron yok, bulut yok. Veriler `%LOCALAPPDATA%\Pegline` altında kalır.

## Durum

Aktif geliştirme (`0.1.0`). Hangi özelliklerin tamamlandığı [ROADMAP.md](ROADMAP.md) dosyasında.

## Şu an çalışanlar

- Üç askı (Görseller / Metinler / Linkler), kavisli ipler, mandallar, sayfalama (görünür alana göre en fazla 9 kart)
- Pano yakalama: metin, tek başına URL → Links, görsel (PNG / DIB; tarayıcıda "Görseli kopyala" gibi yanında adres/metin olan kopyalarda görsel alınır). Gezgin'de kopyalanan görsel dosyaları da toplanır (orijinaller değişmez); diğer dosya kopyaları, gizli, parola yöneticisi ve "geçmişe ekleme" işaretli kopyalar yok sayılır; Pegline'ın kendi kopyaları tekrar yakalanmaz
- Aynı metni tekrar kopyalamak yeni kart açmaz (satır sonu / Unicode biçimine duyarsız kimlik)
- Klavye: oklar, `Tab`, `1–9`, `Alt+←/→`, `Ctrl+←/→`, `Shift+oklar`, `Ctrl+A`, `Enter`, `Ctrl+C`, `Ctrl+Enter` (önceki uygulamaya yapıştır), `P`, `F`, `S`, `Backspace`, `Delete`, `Shift+S` (arama), `Shift+I/T/L`, `Ctrl+Shift+B`, `Esc`
- Kart tıklaması kopyalar, `Ctrl`/`Shift`+tık seçer, çift tık önizler
- `Space` önizleme: görsel zoom (%100–400) ve kaydırma, metni yerinde düzenleme (`E`), linki tarayıcıda açma (`Ctrl+O`)
- `Ctrl+K` Eylemler paleti: tüm komutlar, aranabilir, kısayollarıyla
- Kütüphane (`L`, tepsi): ızgara / liste, Kütüphanem, Favoriler, Düzenlenmemiş, Son kopyalar, Çöp, klasörler, etiketler, arama, tür filtreleri, sıralama, otomatik kaydeden ayrıntı paneli, notlar, geri alınabilir Çöp
- Düzenleme: `Shift+P` klasöre sabitle (son kullanılanlar üstte, `Ctrl+N` yeni klasör), `Alt+P` adlandır ve sabitle (çoklu seçimde sırayla), `Ctrl+Shift+T` etiket ekle / oluştur
- `Ctrl+Shift+E` kopyaları dışa aktar (hiçbir dosyanın üzerine yazmaz), sürükle-bırak: görseller dosya olarak, metin/link metin olarak (`Alt` basılıysa .txt / .url dosyası); Kütüphanede öğeleri kenar çubuğundaki klasöre sürükleyerek ekleme
- `Ctrl+Enter` yapıştırma yalnızca önceki uygulamadaki aynı alana yapılır; parola alanları ve değişmiş hedefler reddedilir (UI Automation, alanın içeriği okunmaz)
- Görsel işaretleme (`M`) ve kırpma (`C`): kalem, fosforlu kalem, dikdörtgen, elips, ok, çizgi, metin; renk / kalınlık / dolgu; orijinal dosya hiç değişmez, işaretler sonradan düzenlenebilir
- Ekran görüntüsü (`Ctrl+Alt+A`), Windows Ekran görüntüleri klasörü ve en fazla 3 izlenen klasör; görsellerdeki yazı cihazda tanınır (Windows OCR), aranabilir ve `Ctrl+Shift+C` ile kopyalanır
- Görsel arama: Kütüphanede görseller içeriğine göre bulunur ("red car", "a cat on a sofa"); Apple MobileCLIP-S2, tamamen cihazda (ONNX Runtime, CPU). Dizinleme düşük öncelikte, tek çekirdekte çalışır ve önde tam ekran bir uygulama (oyun, video) varken bekler
- Link önizlemeleri: başlık, site, yazar ve küçük resim (YouTube dahil); yalnızca herkese açık HTTPS sayfalar, yerel ağ ve anahtar/jeton içeren linkler hiç istenmez
- Ekran kayıtları (MP4 / MOV): Gezgin'den kopyalama, Kütüphaneye sürükleme, Ekran Alıntısı Aracı kayıt klasörü; önizlemede oynatma ve orijinali bozmadan kesme
- Kütüphane filtre paneli: arama yeri (metin ve görsel / yalnızca metin ve OCR / yalnızca görsel), eklenme tarihi, alan adı, etiketler (hepsi eşleşsin), yalnızca sabitlenenler
- Tür başına saklama süresi; sabitlenen, klasördeki ya da etiketli öğeler süresi dolunca silinmez
- Genel kısayollar (`Ctrl+Alt+T` askılar, `Ctrl+Alt+S` arama), tepsi simgesi, tek örnek
- Ayarlar (`Ctrl+,`, tepsi, Eylemler paleti): Genel (dil, görünen askılar, yumuşak renkler / nötr cam, kart boyutu, üst kenarda bekleyince ve yeni bir şey asılınca inme, tam ekran uygulamaların üzerinde, sesler, oturum açılışında başlatma), Geçmiş (tür başına saklama, kaydedilmemişleri temizleme), Kaynaklar (pano, link önizlemeleri, ekran görüntüleri ve kayıtlar, dizinleme, izlenen klasörler), Klavye kısayolları (genel ve gezinme kısayolları için kaydedici, çakışma denetimi), Güncellemeler, Hakkında
- 9 dil: English, Türkçe, Español, Français, Português (Brasil), Azərbaycan dili, 简体中文, 日本語, 한국어; varsayılan olarak Windows görüntü dili

## Geliştirme

```powershell
pwsh scripts/prepare-search-models.ps1   # görsel arama modelleri (bir kez, 383 MB, SHA-256 doğrulamalı)
dotnet test                     # çekirdek + davranış sözleşmesi testleri
dotnet run --project src/Pegline.App
```

Görsel arama modelleri (`assets/CLIP`, Git dışında) derlemeye eklenir; çalışan uygulama hiçbir şey indirmez, sorgu göndermez. Modeller olmadan da derlenir ve çalışır, yalnızca görsel arama kapalı olur.

Debug derlemesi verilerini ayrı bir `Pegline Dev` klasöründe tutar. `PEGLINE_PIN_SHELVES=1` ortam değişkeni, askıların odağı kaybedince kapanmasını engeller (ekran görüntüsü / hata ayıklama için). Debug derlemesinde `Pegline.exe --ocr görsel.png çıktı.txt` OCR'ı tek dosyada dener; `--clip-check çıktı.txt` model dışa aktarımını (kırmızı daire / mavi kare sıralaması, hız) doğrular; `--visual-search "tarif" çıktı.txt` geliştirme kütüphanesini dizinleyip bir tarifi arar; `--links çıktı.txt <url…>` link önizlemelerini dener; `--recording video.mp4 çıktı.txt` içe aktarma ve kesmeyi dener; `Pegline.exe --snapshot <library|library-recent|library-recent-list|shelves|picker-folder|picker-tag|picker-name|markup|markup-crop|library-filters|settings-general|settings-history|settings-sources|settings-shortcuts|settings-updates|settings-about> çıktı.png` bir pencereyi ekran dışında, odak almadan PNG'ye çizer.

## Yapı

| Proje | İçerik |
| --- | --- |
| `src/Pegline.Core` | Model, SQLite deposu, medya deposu, pano kabul kuralları, metin kimliği, askı gezinme durum makinesi, kısayol haritası, Kütüphane sorguları, seçiciler, dışa aktarma ve yapıştırma politikası. UI bağımlılığı yok. |
| `src/Pegline.App` | WPF uygulaması: askı penceresi, Win32 pano dinleyici, genel kısayollar, odak geri verme, tepsi. |
| `tests/Pegline.Core.Tests` | xUnit. `Contracts/` altındaki paylaşılan davranış sözleşmelerini birebir çalıştırır. |

## Kaynak ve atıf

Pegline bağımsız bir uygulamadır. Ürün davranışı, [ClipBoardLine](https://github.com/orkunisitmak/clipboardline-desktop) ve onun temel aldığı [Tendedero](https://github.com/alejandrobujan/tendedero) projelerinden (MIT) esinlenir. Ayrıntılar: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
