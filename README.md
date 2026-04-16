# Source Unpack
> v1.8

<img width="720" height="720" alt="$RNS9TPN" src="https://github.com/user-attachments/assets/57d87644-74d6-42c5-bfd4-b782f810ed67" />




**SourceUnpack** allows you to open any Source Engine map file (`.bsp`) or game archive (`.vpk`) and extract its contents—textures, models, sounds, and more—converting them into standard formats usable in other engines (Unity, Unreal, Blender, Godot).

Built with **.NET 8** and **WPF**, designed to look like the classic GoldSrc tools you know and love.

##  Features

- **SteamCMD Support**: SteamCMD comes integrated with SourceUnpack, select .exe directory in order to get BSP and GMA files from workshop.
- **Mesh, Texture and Audio Preview**: Previews any type of valve assets before extracting. The UI is scalable to the user wish.
- **Extract Everything**: Pulls assets directly from BSP pakfiles, VPK archives or raw GMA files.
- **Smart Conversion**:
    - **Textures**: Converts `.vtf` → `.png` automatically.
    - **Models**: Converts `.mdl` → `.obj` (Wavefront), resolving all mesh data.
    - **Materials**: Parses `.vmt` files to find and extract all required textures.
- **Performance**: 
    - **O(1) Lookup**: Instant search across gigabytes of VPK game data.
    - **Async Loading**: Keeps the UI responsive while chewing through heavy files.
- **Deep Scanning**: Automatically mounts game content from `hl2`, `platform`, and mod folders to find every dependency.
- **Beautiful UI**: A green retro-styled interface inspired by GoldSrc.
- **Extract All / Extract Selected**: batch extraction.
- **Quick Convert**: MDL to OBJ and VTF to PNG.
- **Info panel**: Shows "Map Info" or "Addon Info" based on what's loaded.
- **Specific Miscellaneous Extraction**: Extract BSPs from GMA files and/or convert them to VMFs.

## Usage

### Quick Start
1.  Download the latest release.
2.  Run `SourceUnpack.exe`.
3.  **Set Game Directory**:
    - Point this to your game's root folder (e.g., `.../Counter-Strike Source/cstrike`).
    - This allows the app to find base game assets referenced by the map.
    YOU NEED TO HAVE A SOURCE GAME INSTALLED
4.  **Open a File**:
    - Click **File > Open BSP...** to load a map.
    - Click **File > Open VPK...** to load a game archive.
5.  **Extract**:
    - Browse the asset tree on the right.
    - Right-click any folder or file and select **Extract**.
    - Or use the checkboxes to select multiple items and click **Extract Selected**.

### Output
Extracted files are saved to the `Export` folder in the application directory, preserving the original folder structure (e.g., `materials/concrete/wall01.png`).

## Building from Source

Requirements:
- .NET 8 SDK
- Windows OS (WPF dependency)

```bash
git clone https://github.com/BenVillaEng/SourceUnpack.git
cd SourceUnpack
dotnet build
dotnet run --project src/SourceUnpack.App
```

## Credits

- **Valve Software** for the Source Engine.
- **CommunityToolkit.Mvvm** for the MVVM architecture.
- **SixLabors.ImageSharp** for image processing.
- Made by **BenVillaEng**.

---
*Not affiliated with Valve Corporation.*
