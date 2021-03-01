# xbox-winfsp [![Actions Status](https://github.com/emoose/xbox-winfsp/workflows/CI/badge.svg)](https://github.com/emoose/xbox-winfsp/actions?query=workflow%3ACI)

Brings native (read-only) support for the GDFX, STFS/STFC & FATX Xbox filesystems to Windows, thanks to the power of [WinFsp](https://github.com/billziss-gh/winfsp)!

1. [Download](https://github.com/billziss-gh/winfsp/releases) & install WinFsp
2. [Download](https://github.com/emoose/xbox-winfsp/releases) & extract xbox-winfsp (somewhere you want to keep it installed, ie not a temporary folder!)
3. Right click _INSTALL.bat & run as admin
4. Right click a GDFX/STFS/FATX file and choose "Mount as Xbox STFS/GDF"
5. Enjoy!

## HDD mounting
xbox-winfsp can also mount the partitions from any connected Xbox 360 HDDs, or connected & unlocked Xbox OG HDDs.

To do this, simply connect the HDD to your computer somehow, then right-click the xbox-winfsp.exe and run it as administrator (after performing the setup above).  
This will then iterate over all connected drives on your PC, check if they contain any FATX/STFC partitions, and mount any that do.

ATM though unfortunately any partitions created by WinFsp as admin, can only be accessed by admin...  
To work around this you can eg. run Notepad as admin, then browse the partitions through the open-file-dialog. You can also run a thirdparty file explorer as admin (e.g. 7zip).
Hopefully I can find a proper workaround for this soon, since this reduces usability a huge amount.
(it might have something to do with SDDL security descriptors, nothing I've tried has actually worked yet though :()

### Xbox OG unlocking
Xbox OG HDDs are usually "locked", preventing access to the data inside unless the correct password is provided.
Fortunately devkit drives all share the same password (well, the same all-zero HDD key, which is used to derive the password), however retail drives require the eeprom.bin that's paired with the HDD.

The [xboxhdm23usb](https://sourceforge.net/projects/xboxhdm2/files/xboxhdm23usb/Beta/xboxhdm23usb-Beta2.zip/download) package can be used to unlock HDDs from Windows, simply extract the zip somewhere, place eeprom.bin in the same folder as the bat files, run xboxhd.bat, and then follow the prompts to unlock the drive. (this worked fine for me under Windows 10 64-bit)

For devkit HDDs you can use any devkit EEPROM file, since all should share the same all-zero HDD key: the included [eeprom.bin](https://github.com/emoose/xbox-winfsp/raw/master/eeprom.bin) here is from my own debug kit (with all info except for the encrypted HDD key removed)

After running the unlock command simply follow the "HDD mounting" section above to mount the partitions inside.
(if this fails you might need to unplug/replug the data cable for the HDD after unlocking - though if it still fails after that, you might not have the correct eeprom for the drive...)

## Limitations
- Unfortunately the command-prompt window that appears when you mount an image has to stay running while the image is mounted - closing this window is also currently the only way to unmount the image too.  
(Maybe in a future version we can put this into the system-tray instead, we'll see...)

- Due to STFS files not using file extensions, unfortunately the "Mount as Xbox STFS/GDF" option will have to appear when right clicking any type of file.  
You shouldn't need to worry about accidentally pressing it though, xbox-winfsp only mounts if it detects the file is valid GDFX/STFS.

- Xbox360 USBs containing FATX/XTAF images (Data0000...), and multi-partition FATX images (drive dumps) aren't supported yet.

## Todo
- Add support for GDFX properly - the FS is actually designed as a binary tree, but this doesn't make use of it at all right now... [xbfuse](https://github.com/multimediamike/xbfuse) is a good example of how the tree should be handled.
- Caching support would be nice, seems WinFsp's .NET API was updated with some caching/"FastIO" stuff lately, for some damn reason I installed & coded this against some ancient WinFsp build though >.>
- Change context-menu text, something like "Mount with xbox-winfsp" might be better.
- Read-write ???
