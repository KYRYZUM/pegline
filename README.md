# Pegline

**Kopyaladığın her şey, ipte asılı.** Windows için klavye odaklı, yerel pano geçmişi ve kütüphanesi.

Pegline; görselleri, metinleri ve linkleri ekranın üstünden kayarak inen üç ipe mandallarla asar. Son kopyaların her zaman bir tuş uzağında olur, önemli olanları yerel Kütüphanede saklarsın, hepsini klavyeyle yönetirsin.

> Native .NET 10 / WPF. Electron yok, bulut yok. Veriler `%LOCALAPPDATA%\Pegline` altında kalır.

## Durum

Aktif geliştirme (`0.1.0`). Hangi özelliklerin tamamlandığı [ROADMAP.md](ROADMAP.md) dosyasında.

## Şu an çalışanlar

- Üç askı (Görseller / Metinler / Linkler), kavisli ipler, mandallar, sayfalama (görünür alana göre en fazla 9 kart)
- Pano yakalama: metin, tek başına URL → Links, görsel (PNG / DIB). Gizli, parola yöneticisi, "geçmişe ekleme" işaretli ve dosya kopyaları yok sayılır; Pegline'ın kendi kopyaları tekrar yakalanmaz
- Aynı metni tekrar kopyalamak yeni kart açmaz (satır sonu / Unicode biçimine duyarsız kimlik)
- Klavye: oklar, `Tab`, `1–9`, `Alt+←/→`, `Ctrl+←/→`, `Shift+oklar`, `Ctrl+A`, `Enter`, `Ctrl+C`, `Ctrl+Enter` (önceki uygulamaya yapıştır), `P`, `F`, `S`, `Backspace`, `Delete`, `Shift+S` (arama), `Shift+I/T/L`, `Ctrl+Shift+B`, `Esc`
- Kart tıklaması kopyalar, `Ctrl`/`Shift`+tık seçer, çift tık önizler
- `Space` önizleme: görsel zoom (%100–400) ve kaydırma, metni yerinde düzenleme (`E`), linki tarayıcıda açma (`Ctrl+O`)
- `Ctrl+K` Eylemler paleti: tüm komutlar, aranabilir, kısayollarıyla
- Kütüphane (`L`, tepsi): ızgara / liste, Kütüphanem, Favoriler, Düzenlenmemiş, Son kopyalar, Çöp, klasörler, etiketler, arama, tür filtreleri, sıralama, otomatik kaydeden ayrıntı paneli, notlar, geri alınabilir Çöp
- Tür başına saklama süresi; sabitlenen, klasördeki ya da etiketli öğeler süresi dolunca silinmez
- Genel kısayollar (`Ctrl+Alt+T` askılar, `Ctrl+Alt+S` arama), tepsi simgesi, tek örnek

## Geliştirme

```powershell
dotnet test                     # çekirdek + davranış sözleşmesi testleri
dotnet run --project src/Pegline.App
```

Debug derlemesi verilerini ayrı bir `Pegline Dev` klasöründe tutar. `PEGLINE_PIN_SHELVES=1` ortam değişkeni, askıların odağı kaybedince kapanmasını engeller (ekran görüntüsü / hata ayıklama için). Debug derlemesinde `Pegline.exe --snapshot <library|library-recent|library-recent-list|shelves> çıktı.png` bir pencereyi ekran dışında, odak almadan PNG'ye çizer.

## Yapı

| Proje | İçerik |
| --- | --- |
| `src/Pegline.Core` | Model, SQLite deposu, medya deposu, pano kabul kuralları, metin kimliği, askı gezinme durum makinesi, kısayol haritası. UI bağımlılığı yok. |
| `src/Pegline.App` | WPF uygulaması: askı penceresi, Win32 pano dinleyici, genel kısayollar, odak geri verme, tepsi. |
| `tests/Pegline.Core.Tests` | xUnit. `Contracts/` altındaki paylaşılan davranış sözleşmelerini birebir çalıştırır. |

## Kaynak ve atıf

Pegline bağımsız bir uygulamadır. Ürün davranışı, [ClipBoardLine](https://github.com/orkunisitmak/clipboardline-desktop) ve onun temel aldığı [Tendedero](https://github.com/alejandrobujan/tendedero) projelerinden (MIT) esinlenir. Ayrıntılar: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
