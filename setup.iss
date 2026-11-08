; AlbionCompanion 安装脚本
; 使用 Inno Setup 6 编译

[Setup]
AppName=Albion Companion
AppVersion=1.0.0
AppPublisher=Albion Companion
DefaultDirName={pf}\AlbionCompanion
DefaultGroupName=Albion Companion
OutputBaseFilename=AlbionCompanion_Setup_v1.0.0
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
OutputDir=.\installer

[Files]
Source: "bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Albion Companion"; Filename: "{app}\AlbionCompanion.exe"
Name: "{group}\卸载 Albion Companion"; Filename: "{uninstallexe}"
Name: "{commondesktop}\Albion Companion"; Filename: "{app}\AlbionCompanion.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"

[Run]
Filename: "{app}\AlbionCompanion.exe"; Description: "立即启动 Albion Companion"; Flags: nowait postinstall skipifsilent
