# NdiMerger LPM

Standalone Windows app that composites **NDI**, **Spout**, **HDMI/capture-card**, and **Browser** sources onto the MAM pixelmap (**8038×5798**) at **native input resolution**, then sends the full frame as an **NDI** output.

## Features

- Fixed canvas from `assets/pixelmap.json` (Floor, Wall West 1–3, Sud, Est, Nord, Stage)
- Sources placed 1:1 in pixels (`Scale = 1` = native resolution)
- Zone snap presets: **Native** / **FitZone** / **FillZone**
- Free drag placement on the preview
- Live resolution updates when a source changes size
- Layout save/load (JSON)
- NDI sender (default name `MAM-Pixelmap`)
- **Browser source** (OBS-style): load a URL in an embedded WebView2 — page content only, no tabs/address bar/title bar

## Requirements

- Windows 10/11 x64
- .NET 10 SDK
- [NDI Runtime](https://ndi.video/) (or ship `Processing.NDI.Lib.x64.dll` next to the EXE — already copied from `libs/`)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (Evergreen — usually preinstalled on Windows 11)
- GPU with Direct3D 11
- Optional: Spout senders, DirectShow capture devices (Elgato / Magewell / webcam)

## Build & run

```powershell
cd d:\lpm\ndimergerLPM
dotnet build src\NdiMerger.App\NdiMerger.App.csproj -c Release
dotnet run --project src\NdiMerger.App\NdiMerger.App.csproj -c Release
```

## Usage

1. Click **Refresh** to discover NDI / Spout / capture sources
2. Select a source → **Add**
3. Or click **Browser** → enter URL and size (default 1920×1080) → **OK**
4. Optionally pick a zone + scale mode → **Snap selected to zone**
5. Or drag the layer on the preview
6. Enable **Send NDI** and receive `MAM-Pixelmap` in Resolume / OBS / LED software

## Notes

- Full-frame NDI at 8038×5798 is very bandwidth-heavy; expect ~15–30 fps depending on GPU/network
- Wall West 2 uses an axis-aligned bounding box (diagonal cut not masked in v1)
- Place a full-resolution `assets/pixelmap.jpg` (or PNG) for accurate alignment overlay
- Browser layers are persisted in the session layout; restoring reopens the same URL in a new WebView2 instance
