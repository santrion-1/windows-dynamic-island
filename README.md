# Windows Dynamic Island

A lightweight, native Dynamic Island desktop overlay for Windows 10/11 built with C# and WPF (.NET 10) with zero external NuGet dependencies.


##  How to Run
1. Make sure you have **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** installed on Windows 10/11.
2. Clone this repository and run it via terminal:
   ```powershell
   git clone https://github.com/santrion-1/windows-dynamic-island.git
   cd windows-dynamic-island
   dotnet run
   ```

---

## Controls & Shortcuts
- **Hover Mouse:** Expand the Dynamic Island.
- **Move Mouse Away:** Automatically collapse back into the compact pill.
- **Mouse Wheel / `<` `>` Buttons / Left-Right Arrow Keys:** Switch between pages.
- **Hold Left-Click & Drag Left/Right:** Move the Dynamic Island horizontally along the top screen edge.
- **Right-Click Menu:** Pin/Unpin window (stay expanded), Reset position to center, jump to specific pages, or Exit.
- **`P` Key:** Toggle Pin mode.
- **`Ctrl + F`:** Open Quick Web Search.

---

##  Inspiration & Credits
- UI/UX design inspired by Apple's Dynamic Island and desktop notch utilities such as **Nusa**.
- Written in native C# / WPF using Windows Media (GSMTC), Win32 Power, and CoreAudio APIs.
