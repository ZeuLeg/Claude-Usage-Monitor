# Claude Usage Monitor

## Ilke

Guven > ozellik. Bu arac haftalarca acik kalir: sizinti/birikme kabul edilemez, hata durumlari sessiz ve zarif olmali, kullaniciya ham exception gosterilmez. Yeni ozellik eklerken "bu widget'in isi mi?" diye sor — minimal yuzey korunur, detay hover karti gibi istege bagli yuzeylere gider.

C# WinForms (net10.0-windows) — Claude kullanim limitini Windows taskbar'i ustunde kucuk bir layered widget'ta gosterir.

## Komutlar

- Build: `dotnet build ClaudeUsageMonitor.sln -c Release` — sln adi SART: kokte hem .sln hem .csproj var, ciplak `dotnet build` MSB1011 verir
- Test: `dotnet test ClaudeUsageMonitor.sln` (xunit, Windows-only target)
- Calistir: `dotnet run --project ClaudeUsageMonitor.csproj`

## Mimari (src/)

- `Api/` — OAuth credential okuma + usage endpoint fetch (`UsageFetcher`), `seven_day_opus` opsiyonel
- `Polling/` — `UsagePoller` (2 dk aralik + reset anina hizali ekstra poll), `NotificationEvaluator`, `BurnRateTracker`
- `Ui/TaskbarWidget` — WS_EX_TOPMOST + WS_EX_LAYERED bagimsiz pencere; render `UpdateLayeredWindow` ile
- `App/MainForm` — gorunmez koordinator form; tray icon, event wiring
- `Infra/` — Settings (`%LOCALAPPDATA%/ClaudeUsageMonitor/settings.json`), Win32 P/Invoke, log

## Gotcha'lar

- Widget topmost'lugu Win11'de sessizce dusebilir → 2 sn'lik timer `SetWindowPos(HWND_TOPMOST)` ile yeniden assert eder; bu timer'i kaldirma
- Konum `TrayNotifyWnd`'ye ankorlu; bulunamazsa taskbar sag kenari → o da yoksa mevcut konum korunur. `ComputePosition`'a fallback'siz kod ekleme (widget X=0'a kayar — eski bug)
- `NotificationEvaluator` enjekte edilebilir saat kullanir (`Func<DateTime> utcNow`) — testlerde `Thread.Sleep` yazma, saati enjekte et
- `BurnRateTracker` hem poller thread'inden hem UI thread'inden erisilir — tum uyeler `_gate` lock'u altinda kalmali
- Reset bildirimi iki yoldan tetiklenir: lokal-timer (resetsAt gecince) + API-driven (yeni pencere). Dedupe `_localResetFiredFor` ile — birini degistirirken ikisini birlikte dusun
- Release: versiyon csproj'da bump edilir, `v`-prefix'li tag release workflow'unu tetikler
- `tests/**/bin|obj` cop dosyalarla dolu — arama yaparken haric tut
