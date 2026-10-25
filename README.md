# PopeManager

Connect an authorized, unpatched Quest. Steam sign-in opens automatically. Sign in using
QR or username/password; enter a Steam Guard code if requested. PopeManager uses the
bundled DepotDownloader to fetch the latest public manifest for app **1533390**, depot
**1533392** (Steam Frame), validates the Android ARM64 APK set, generates IL2CPP libraries,
patches, signs, and installs it onto the headset. An existing headset installation is
optional. Its APKs, expansion files, and plugins are backed up for recovery, never used
as the patch source. Downloaded OBB files are installed with the downloaded APK build.
Missing required expansion data or invalid APKs stops preparation before uninstalling.
After sign-in, download, patching, signing, and installation continue automatically.
Cancelling or a failure leaves a **Sign in with Steam** retry button; reconnecting the
headset starts a fresh automatic attempt. Already patched headsets open mod management.

The Steam login page is adapted from GTCLauncher. Steam Guard prompts, QR refresh,
download progress and cancellation are supported. Headless `--install` prints the login
QR to its console. No manifest ID is pinned; every preparation resolves the current
public manifest through Steam. Depot access requires an entitled Steam account.

Build with `dotnet build PopeManager.csproj -c Release`. DepotDownloader binaries and
licenses in `depotdownloader/` and `depotdownloader-linux/` are copied to build/publish
outputs. On other platforms, provide `DepotDownloader` on PATH. Sessions and recovery
backups are under `%LOCALAPPDATA%/PopeMods/PopeManager` (or `POPEMANAGER_DATA`).

Steam Frame APK/XR compatibility still requires a physical Quest test.

Run the downloader and authentication checks with `dotnet run --project Tests/SteamFrameChecks/SteamFrameChecks.csproj`. They use a fake downloader process and do not access Steam or a headset.
