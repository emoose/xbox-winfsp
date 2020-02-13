# xbox-winfsp [![Actions Status](https://github.com/emoose/xbox-winfsp/workflows/CI/badge.svg)](https://github.com/emoose/xbox-winfsp/actions?query=workflow%3ACI)

Brings native (read-only) support for the GDFX, STFS/STFC & FATX Xbox filesystems to Windows, thanks to the power of [WinFsp](https://github.com/billziss-gh/winfsp)!

1. [Download](https://github.com/billziss-gh/winfsp/releases) & install WinFsp
2. [Download](https://github.com/emoose/xbox-winfsp/releases) & extract xbox-winfsp (somewhere you want to keep it installed, ie not a temporary folder!)
3. Right click _INSTALL.bat & run as admin
4. Right click a GDFX/STFS/FATX file and choose "Mount as Xbox STFS/GDF"
5. Enjoy!

## Xbox HDD mounting
xbox-winfsp can also mount the partitions from any connected Xbox 360 HDDs.
(no support for Xbox OG HDDs unfortunately - though it might be possible with devkit HDDs later on... see Limitations section below)

To do this, simply connect the HDD to your computer somehow, then right-click the xbox-winfsp.exe and run it as administrator (after performing the setup above).  
This will then iterate over all connected drives on your PC, check if they contain any FATX/STFC partitions, and mount any that do.

ATM though unfortunately any partitions created by WinFsp as admin, can only be accessed by admin...  
To work around this you can eg. run Notepad as admin, then browse the partitions through the open-file-dialog.  
Hopefully I can find a proper workaround for this soon, since this reduces usability a huge amount. (it might have something to do with SDDL security descriptors, nothing I've tried has actually worked yet though :()

## Limitations
- Unfortunately the command-prompt window that appears when you mount an image has to stay running while the image is mounted - closing this window is also currently the only way to unmount the image too.  
(Maybe in a future version we can put this into the system-tray instead, we'll see...)

- Due to STFS files not using file extensions, unfortunately the "Mount as Xbox STFS/GDF" option will have to appear when right clicking any type of file.  
You shouldn't need to worry about accidentally pressing it though, xbox-winfsp only mounts if it detects the file is valid GDFX/STFS.

- Xbox OG HDDs aren't supported as the HDD is "locked", using a special key only known to the Xbox.  
IIRC devkit HDDs might use an all-zero key though, so it might be possible to add support for those, and any HDDs where the user knows the key, or has zeroed it themself.  
(No idea if we can actually perform the unlock while running Windows though - need to investigate it some time)

- Xbox360 USBs containing FATX/XTAF images (Data0000...) aren't supported yet.

## Todo
- Add support for GDFX properly - the FS is actually designed as a binary tree, but this doesn't make use of it at all right now... [xbfuse](https://github.com/multimediamike/xbfuse) is a good example of how the tree should be handled.
- Caching support would be nice, seems WinFsp's .NET API was updated with some caching/"FastIO" stuff lately, for some damn reason I installed & coded this against some ancient WinFsp build though >.>
- Change context-menu text, something like "Mount with xbox-winfsp" might be better.
- Read-write ???
