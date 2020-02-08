# xbox-winfsp [![Actions Status](https://github.com/emoose/xbox-winfsp/workflows/CI/badge.svg)](https://github.com/emoose/xbox-winfsp/actions?query=workflow%3ACI)

Brings native (read-only) support for the GDFX, STFS & FATX Xbox filesystems to Windows, thanks to the power of [WinFsp](https://github.com/billziss-gh/winfsp)!

1. [Download](https://github.com/billziss-gh/winfsp/releases) & install WinFsp
2. [Download](https://github.com/emoose/xbox-winfsp/releases) & extract xbox-winfsp (somewhere you want to keep it installed, ie not a temporary folder!)
3. Right click _INSTALL.bat & run as admin
4. Right click a GDFX/STFS/FATX file and choose "Mount as Xbox STFS/GDF"
5. Enjoy!

## Limitations
- Unfortunately the command-prompt window that appears when you mount an image has to stay running while the image is mounted - closing this window is also currently the only way to unmount the image too.  
(Maybe in a future version we can put this into the system-tray instead, we'll see...)

- Due to STFS files not using file extensions, unfortunately the "Mount as Xbox STFS/GDF" option will have to appear when right clicking any type of file.  
You shouldn't need to worry about accidentally pressing it though, xbox-winfsp only mounts if it detects the file is valid GDFX/STFS.

- Right now FATX support has only been coded to work with single-partition FATX image files (eg. xlaunch.fdf) - hopefully later down the line we can support Xbox360 HDDs/USBs/backups/etc, but atm those won't work at all.

## Todo
- Add support for GDFX properly - the FS is actually designed as a binary tree, but this doesn't make use of it at all right now... [xbfuse](https://github.com/multimediamike/xbfuse) is a good example of how the tree should be handled.
- Caching support would be nice, seems WinFsp's .NET API was updated with some caching/"FastIO" stuff lately, for some damn reason I installed & coded this against some ancient WinFsp build though >.>
- Change context-menu text, something like "Mount with xbox-winfsp" might be better.
- Read-write ???
