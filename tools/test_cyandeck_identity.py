#!/usr/bin/env python3
"""Guard standalone CyanDeck names without inspecting old user data."""
from pathlib import Path
import xml.etree.ElementTree as ET
import json
root=Path(__file__).resolve().parents[1]
source=root/'src'/'CyanDeck'
p=ET.parse(source/'CyanDeck.csproj').getroot()
xml={e.tag:e.text for e in p.iter() if e.text and e.tag in ('AssemblyName','RootNamespace','Product','Version')}
assert xml['AssemblyName']==xml['RootNamespace']==xml['Product']=='CyanDeck'
parts = tuple(int(v) for v in xml['Version'].split('.'))
assert len(parts) == 3 and parts >= (8, 0, 0)
manifest = json.loads((root/'update-manifest.example.json').read_text(encoding='utf-8'))
assert manifest['version'] == xml['Version']
assert (source/'AppIcon.ico').is_file()
assert not (root/'src'/'DesktopDeck').exists()
assert not (source/'Services'/'CyanDeckCutoverJournal.cs').exists()
assert not (root/'src'/'CyanDeck.Launcher').exists()
paths=(source/'Services'/'DataRootPaths.cs').read_text(encoding='utf-8')
assert 'ActiveRoaming' in paths and 'ActiveLocal' in paths
assert '"CyanDeck"' in paths and '"DesktopDeck"' not in paths
app=(source/'App.xaml.cs').read_text(encoding='utf-8')
assert 'CyanDeck.Activation.v1' in app and 'CyanDeckSingleInstanceGate.TryAcquire()' in app
assert 'LegacyProfileWriterGate' not in app
oauth=(source/'Calendar'/'GoogleOAuthClientConfiguration.cs').read_text(encoding='utf-8')
assert 'CyanDeck.OAuthDesktopClient' in oauth
csproj = (source/'CyanDeck.csproj').read_text(encoding='utf-8')
updater = (source/'Services'/'UpdateService.cs').read_text(encoding='utf-8')
package = (root/'package.ps1').read_text(encoding='utf-8-sig')
assert 'CyanDeck.TrustedUpdatePublicKey' in csproj and 'CyanDeck.TrustedUpdatePublicKey' in updater
assert 'ReleasePublicKeyPath' in package and 'embeddedKeyFingerprint' in package
assert 'VerifyData(' in updater
assert 'CyanDeck.BundledUpdaterScript' in csproj and 'CyanDeck.BundledUpdaterScript' in updater
assert 'GetManifestResourceStream("CyanDeck.BundledUpdaterScript")' in updater
assert updater.count('var script = PrepareBundledUpdaterScript();') == 3
for name in ['build.ps1','package.ps1','install.ps1','uninstall.ps1','apply-update.ps1']:
    assert 'CyanDeck.exe' in (root/name).read_text(encoding='utf-8-sig'),name
release=(source/'Services'/'GitHubReleaseSource.cs').read_text(encoding='utf-8')
assert 'github-release://deadpixel134/CyanDeck' in release
assert 'github-release://deadpixel134/DesktopDeck' not in release
assert 'RequireNativeCyanDeckVersion' in release
assert 'github-release-beta://deadpixel134/CyanDeck' in release
assert 'EnableBetaUpdateChannel' in (source/'Models'/'Models.cs').read_text(encoding='utf-8')
assert 'BetaUpdateChannelCheck' in (source/'Views'/'SettingsWindow.xaml').read_text(encoding='utf-8')
# Calendar grid never exposes scrollbars during interactive resize, while dense
# event lanes remain accessible with wheel, trackpad and touch panning.
calendar_window = (source/'Calendar'/'CalendarWindow.cs').read_text(encoding='utf-8')
assert 'VerticalScrollBarVisibility = ScrollBarVisibility.Hidden' in calendar_window
assert 'HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled' in calendar_window
assert 'PanningMode = PanningMode.VerticalOnly' in calendar_window
assert '_longPressTimer = new() { Interval = TimeSpan.FromSeconds(1) }' in calendar_window
assert 'Only capture after the explicit 1-second hold' in calendar_window
assert 'TimeSpan.FromSeconds(2)' not in calendar_window.split('_longPressTimer = new()',1)[1].split(';',1)[0]
assert '_weekScroll.ScrollToTop();' in calendar_window
assert 'new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }' in calendar_window
print('PASS: standalone CyanDeck namespace, executable, storage, OAuth and original icon')
