using Microsoft.Win32;
using PersonalTools;

// --probe only reads the real keyboard controller. --live writes to it and puts every value back
// (brightness, logo, profile, profile 2's keyboard effect); the keys visibly change for a moment
// when Windows Dynamic Lighting is off, so tell the user before running it.
if (args.Contains("--probe"))
{
    using var device = Open();
    var layout = device.ReadLayout();
    int profile = device.ReadProfile();
    var effects = device.ReadEffects(profile);
    Console.WriteLine($"brightness {device.ReadBrightness()}, profile {profile}, logo {(device.ReadLogo() ? "on" : "off")}");
    Console.WriteLine($"matrix {layout.Rows.Length}x{layout.Rows.Max(r => r.Length)}, keys {layout.Keys(LightingZone.Keyboard).Length}, bar {layout.Keys(LightingZone.Bar).Length}, logo {layout.Keys(LightingZone.Logo).Length}");
    foreach (var zone in LightingZones.All)
    {
        var state = LightingZones.Describe(effects, zone, LightingZones.Keys(layout, effects, zone).Length);
        Console.WriteLine($"{LightingController.Name(zone)}: {(state.Kind is { } k ? LightingEffects.Name(k) : "없음")} {state.Color?.Hex ?? (state.Random ? "무작위" : "")} speed {state.Speed}");
    }
    Console.WriteLine($"Windows Dynamic Lighting controls the laptop: {new WindowsDynamicLighting().Controls()?.ToString() ?? "unknown"} (this process may see a virtualized HKCU)");
    var colors = device.ReadColors();
    Console.WriteLine(colors is null ? "colors: report busy" : $"colors: {colors.Count} lights, {colors.Values.Distinct().Count()} distinct");
    return;
}
if (args.Contains("--live"))
{
    using var device = Open();
    int brightness = device.ReadBrightness(), profile = device.ReadProfile();
    bool logo = device.ReadLogo();
    var saved = Enumerable.Range(1, SpectrumPacket.Profiles).ToDictionary(p => p, device.ReadEffects);
    Console.WriteLine($"start: brightness {brightness}, profile {profile}, logo {logo}");
    try
    {
        int other = brightness == SpectrumPacket.MaxBrightness ? brightness - 1 : brightness + 1;
        device.SetBrightness(other);
        Console.WriteLine($"{(device.ReadBrightness() == other ? "PASS" : "FAIL")} brightness {other}");
        device.SetLogo(!logo);
        Console.WriteLine($"{(device.ReadLogo() == !logo ? "PASS" : "FAIL")} logo {!logo}");
        int next = profile == 2 ? 3 : 2;
        device.SetProfile(next);
        Console.WriteLine($"{(device.ReadProfile() == next ? "PASS" : "FAIL")} profile {next}");
        var layout = device.ReadLayout();
        var keys = LightingZones.Keys(layout, saved[2], LightingZone.Keyboard);
        var red = LightingZones.Build(LightingEffectKind.Static, new(255, 0, 0), false, 0, null, keys);
        var written = LightingZones.Compose(saved[2], LightingZone.Keyboard, red, z => LightingZones.Keys(layout, saved[2], z));
        device.WriteEffects(2, written);
        var back = device.ReadEffects(2);
        Console.WriteLine($"{(back.Length == written.Length && back.Zip(written).All(p => p.First.Equivalent(p.Second)) ? "PASS" : "FAIL")} profile 2 keyboard red");
    }
    finally
    {
        device.WriteEffects(2, saved[2]);
        var restored = device.ReadEffects(2);
        Console.WriteLine($"{(restored.Length == saved[2].Length && restored.Zip(saved[2]).All(p => p.First.Equivalent(p.Second)) ? "PASS" : "FAIL")} profile 2 restored");
        device.SetProfile(profile);
        device.SetLogo(logo);
        device.SetBrightness(brightness);
        Console.WriteLine($"end: brightness {device.ReadBrightness()}, profile {device.ReadProfile()}, logo {device.ReadLogo()}");
    }
    return;
}

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action run) { run(); Console.WriteLine("PASS " + name); passed++; }

long now = 1_000;
long Clock() => now;
void Sleep(int ms) => now += Math.Max(ms, 1);
(FakeFirmware, SpectrumClient) Device(Action<FakeFirmware>? setup = null)
{
    var firmware = new FakeFirmware();
    setup?.Invoke(firmware);
    return (firmware, new SpectrumClient(firmware, Clock, Sleep));
}
(LightingController, FakeFirmware, FakeWindows) Controller(Action<FakeFirmware>? setup = null, bool found = true)
{
    var firmware = new FakeFirmware();
    setup?.Invoke(firmware);
    var windows = new FakeWindows();
    var files = new LightingFiles(Path.Combine(Path.GetTempPath(), "PersonalToolsLightingTests", Guid.NewGuid().ToString("N")));
    var controller = new LightingController(() => found ? new SpectrumClient(firmware, Clock, Sleep) : null, windows, files, Clock);
    controller.Shown();
    return (controller, firmware, windows);
}

Test("parses the factory profiles read from this laptop", () =>
{
    var p1 = SpectrumPacket.ParseEffects(FakeFirmware.Answer(1, FakeFirmware.Factory[1]));
    Check(p1.Length == 2, "profile 1 has two effects");
    Check(p1[0].Kind == LightingEffectKind.ScrewRainbow && p1[0].Speed == 2 && p1[0].Clockwise == 1 && p1[0].Keys.Length == 101, "keyboard screw rainbow");
    Check(p1[1].Kind == LightingEffectKind.RainbowWave && p1[1].Direction == 3 && p1[1].Keys.Length == 29, "bar and logo rainbow wave");
    var p2 = SpectrumPacket.ParseEffects(FakeFirmware.Answer(2, FakeFirmware.Factory[2]));
    Check(p2.Length == 1 && p2[0].Kind == LightingEffectKind.Static && p2[0].Colors.Single() == LightingColor.LegionBlue && p2[0].Keys.Length == 130, "profile 2 static blue on all 130 codes");
    for (int p = 1; p <= 6; p++)
    {
        var parsed = SpectrumPacket.ParseEffects(FakeFirmware.Answer(p, FakeFirmware.Factory[p]));
        Check(SpectrumPacket.Encode(parsed).SequenceEqual(FakeFirmware.Factory[p][3..]), $"profile {p} encodes back to the same bytes");
    }
});
Test("rejects an effect whose marker bytes are wrong", () =>
{
    var answer = FakeFirmware.Answer(2, FakeFirmware.Factory[2]);
    answer[8] = 9;
    try { SpectrumPacket.ParseEffects(answer); throw new Exception("accepted"); } catch (InvalidDataException) { }
});
Test("an effect request carries the real payload length", () =>
{
    var effects = SpectrumPacket.ParseEffects(FakeFirmware.Answer(1, FakeFirmware.Factory[1]));
    var request = SpectrumPacket.EffectRequest(4, effects);
    var body = SpectrumPacket.Encode(effects);
    Check(request.Length == 960 && request[0] == 7 && request[1] == 0xCB, "header");
    Check(BitConverter.ToUInt16(request, 2) == 3 + body.Length, "u16 length");
    Check(request[4] == 4 && request[5] == 1 && request[6] == 1, "profile and fixed bytes");
    Check(request.Skip(7).Take(body.Length).SequenceEqual(body) && request.Skip(7 + body.Length).All(b => b == 0), "body then zeros");
    Check(SpectrumPacket.Request(0xCD).Take(4).SequenceEqual(new byte[] { 7, 0xCD, 0xC0, 3 }), "plain requests keep C0 03");
});
Test("zones from the factory profiles", () =>
{
    var p1 = SpectrumPacket.ParseEffects(FakeFirmware.Answer(1, FakeFirmware.Factory[1]));
    Check(LightingZones.Describe(p1, LightingZone.Keyboard, 101).Kind == LightingEffectKind.ScrewRainbow, "keyboard");
    Check(LightingZones.Describe(p1, LightingZone.Bar, 10).Kind == LightingEffectKind.RainbowWave, "bar");
    Check(LightingZones.Describe(p1, LightingZone.Logo, 1).Kind == LightingEffectKind.RainbowWave, "logo");
    var p3 = SpectrumPacket.ParseEffects(FakeFirmware.Answer(3, FakeFirmware.Factory[3]));
    var bar = LightingZones.Describe(p3, LightingZone.Bar, 10);
    Check(bar.Kind == LightingEffectKind.ColorPulse && bar.Random && bar.Speed == 2, "profile 3 bar breathes in random colors");
    var p2 = SpectrumPacket.ParseEffects(FakeFirmware.Answer(2, FakeFirmware.Factory[2]));
    Check(LightingZones.All.All(z => LightingZones.Describe(p2, z, 1).Color == LightingColor.LegionBlue), "profile 2 blue everywhere");
    var none = LightingZones.Describe([], LightingZone.Logo, 1);
    Check(none.Kind is null, "no effect");
});
Test("changing one zone keeps the others on their own keys", () =>
{
    var layout = FakeFirmware.Layout();
    var p1 = SpectrumPacket.ParseEffects(FakeFirmware.Answer(1, FakeFirmware.Factory[1]));
    var keys = layout.Keys(LightingZone.Keyboard);
    var red = LightingZones.Build(LightingEffectKind.Static, new(255, 0, 0), false, 2, LightingZones.EffectFor(p1, LightingZone.Keyboard), keys);
    var next = LightingZones.Compose(p1, LightingZone.Keyboard, red, layout.Keys);
    Check(next.Length == 3, "one effect per zone");
    Check(next[0].Kind == LightingEffectKind.Static && next[0].Speed == 0 && next[0].Colors.Single() == new LightingColor(255, 0, 0) && next[0].Keys.Length == 101, "keyboard red, no speed");
    Check(next[1].Kind == LightingEffectKind.RainbowWave && next[1].Direction == 3 && next[1].Keys.SequenceEqual(layout.Keys(LightingZone.Bar)), "bar keeps its wave");
    Check(next[2].Kind == LightingEffectKind.RainbowWave && next[2].Keys.SequenceEqual(new ushort[] { SpectrumCodes.Logo }), "logo keeps its wave on its own code");
    Check(next.SelectMany(e => e.Keys).All(k => k != 0) && next.SelectMany(e => e.Keys).Distinct().Count() == 112, "no zero codes, no overlap");
});
Test("audio effects turn white when another zone changes", () =>
{
    var layout = FakeFirmware.Layout();
    var p5 = SpectrumPacket.ParseEffects(FakeFirmware.Answer(5, FakeFirmware.Factory[5]));
    Check(LightingEffects.NeedsLenovoApp(LightingZones.Describe(p5, LightingZone.Bar, 10).Kind!.Value), "profile 5 is audio");
    var pulse = LightingZones.Build(LightingEffectKind.ColorPulse, LightingColor.LegionBlue, false, 3, null, layout.Keys(LightingZone.Bar));
    var next = LightingZones.Compose(p5, LightingZone.Bar, pulse, layout.Keys);
    Check(next[0].Kind == LightingEffectKind.Static && next[0].Colors.Single() == LightingColor.White && next[0].Keys.Length == 101, "keyboard white");
    Check(next[1] == pulse, "bar is the new effect");
    Check(next[2].Kind == LightingEffectKind.Static && next[2].Keys.Single() == SpectrumCodes.Logo, "logo white");
});
Test("an effect only carries the settings it takes", () =>
{
    var wave = new SpectrumEffect(LightingEffectKind.RainbowWave, 2, 0, 4, LightingColorMode.None, [], []);
    var built = LightingZones.Build(LightingEffectKind.ColorWave, LightingColor.White, false, 9, wave, [1]);
    Check(built.Direction == 4 && built.Speed == 3 && built.Clockwise == 0 && built.ColorMode == LightingColorMode.List, "direction kept, speed clamped");
    var screw = LightingZones.Build(LightingEffectKind.ScrewRainbow, null, false, 1, null, [1]);
    Check(screw.Clockwise == 1 && screw.Direction == 0 && screw.ColorMode == LightingColorMode.None && screw.Colors.Length == 0, "rainbow has rotation, no colors");
    var random = LightingZones.Build(LightingEffectKind.Rain, LightingColor.White, true, 2, null, [1]);
    Check(random.ColorMode == LightingColorMode.Random && random.Colors.Length == 0, "random has no color list");
    var keep = LightingZones.Build(LightingEffectKind.ColorPulse, null, false, 2, new(LightingEffectKind.Static, 0, 0, 0, LightingColorMode.List, [new(1, 2, 3)], []), [1]);
    Check(keep.Colors.Single() == new LightingColor(1, 2, 3), "color kept when none is given");
});
Test("the client skips answers that belong to the previous request", () =>
{
    var (firmware, client) = Device(f => { f.Brightness = 6; f.Profile = 3; f.Lag = 2; });
    Check(client.ReadProfile() == 3, "profile");
    Check(client.ReadBrightness() == 6, "brightness, not the stale profile answer");
    Check(client.ReadEffects(4)[0].Kind == LightingEffectKind.Rain, "profile 4");
    Check(client.ReadEffects(3)[0].Kind == LightingEffectKind.Ripple, "profile 3, not the stale profile 4 answer");
    Check(firmware.SetWhileBusy == 0, "every request waited for the colors to come back");
});
Test("the client asks again when the controller misses a request", () =>
{
    var (firmware, client) = Device(f => f.DropNext = 2);
    Check(client.ReadBrightness() == 9, "answered on the third try");
    var (silent, quiet) = Device(f => f.DropNext = 99);
    try { quiet.ReadBrightness(); throw new Exception("no timeout"); } catch (TimeoutException) { }
});
Test("the client reads the key matrix", () =>
{
    var (_, client) = Device();
    var layout = client.ReadLayout();
    Check(layout.Rows.Length == 9 && layout.Rows.All(r => r.Length == 22), "9x22");
    Check(layout.Keys(LightingZone.Keyboard).Length == 101 && layout.Keys(LightingZone.Bar).Length == 10, "101 keys, 10 bar segments");
    Check(layout.Keys(LightingZone.Logo).SequenceEqual(new ushort[] { SpectrumCodes.Logo }), "logo from the extra row");
});
Test("writes reach the firmware", () =>
{
    var (firmware, client) = Device();
    client.SetBrightness(12);
    Check(firmware.Brightness == 9, "brightness clamped to 9");
    client.SetBrightness(4);
    client.SetLogo(false);
    client.SetProfile(5);
    Check(firmware.Brightness == 4 && !firmware.Logo && firmware.Profile == 5, "brightness, logo, profile");
    var effects = new[] { new SpectrumEffect(LightingEffectKind.Static, 0, 0, 0, LightingColorMode.List, [new(9, 8, 7)], [1, 2, 3]) };
    client.WriteEffects(2, effects);
    var back = client.ReadEffects(2);
    Check(back.Length == 1 && back[0].Equivalent(effects[0]), "profile 2 written");
    client.ResetProfile(2);
    Check(SpectrumPacket.Encode(client.ReadEffects(2)).SequenceEqual(FakeFirmware.Factory[2][3..]), "profile 2 back to factory");
    try { client.SetProfile(0); throw new Exception("profile 0 accepted"); } catch (ArgumentOutOfRangeException) { }
});
Test("live colors come from the state report only", () =>
{
    var (firmware, client) = Device(f => f.Logo = false);
    var colors = client.ReadColors()!;
    Check(colors[SpectrumCodes.Logo] == LightingColor.Black, "logo off reads black");
    Check(colors.Count == 112 + 18, "keys, bar, logo and the dead vent codes");
    client.ReadBrightness();
    // Right after an answer the report still holds it.
    firmware.Lag = 0;
    _ = client.Ask(0xCA);
    Check(client.ReadColors() is null, "busy report gives no colors");
});

Test("the controller reads nothing until the page is shown", () =>
{
    var (controller, firmware, _) = Controller();
    controller.Hidden();
    controller.Step(poll: false, frame: false);
    Check(firmware.Requests == 0 && !controller.Snapshot.Ready, "no device traffic");
});
Test("a poll fills the page", () =>
{
    var (controller, _, _) = Controller(f => { f.Brightness = 7; f.Profile = 1; });
    LightingSnapshot? seen = null;
    controller.Changed += s => seen = s;
    controller.Step(poll: true, frame: false);
    var s = controller.Snapshot;
    Check(s.Ready && s.Found && s.Brightness == 7 && s.Profile == 1 && s.LogoOn && !s.WindowsControls, "values");
    Check(s.Zones.Length == 3 && s.Zones[0].Kind == LightingEffectKind.ScrewRainbow && s.Zones[0].KeyCount == 101 && s.Zones[1].KeyCount == 10, "zones");
    Check(s.Layout is not null && seen == s, "layout and change event");
});
Test("the page redraws only when something changes", () =>
{
    var (controller, firmware, _) = Controller();
    controller.Step(poll: true, frame: false);
    int changes = 0;
    controller.Changed += _ => changes++;
    controller.Step(poll: true, frame: false);
    controller.Step(poll: true, frame: false);
    Check(changes == 0, $"changed {changes} times without a change");
    firmware.Logo = false;
    controller.Step(poll: true, frame: false);
    Check(changes == 1 && !controller.Snapshot.LogoOn, "logo change redraws once");
});
Test("Fn+Space is picked up on the next poll", () =>
{
    var (controller, firmware, _) = Controller();
    controller.Step(poll: true, frame: false);
    firmware.Profile = 4;
    firmware.Brightness = 2;
    controller.Step(poll: true, frame: false);
    var s = controller.Snapshot;
    Check(s.Profile == 4 && s.Brightness == 2 && s.Zones[0].Kind == LightingEffectKind.Rain, "profile 4 read with its effects");
});
Test("a zone change rewrites the current profile", () =>
{
    var (controller, firmware, _) = Controller();
    controller.Step(poll: true, frame: false);
    controller.SetZone(LightingZone.Keyboard, 1, kind: LightingEffectKind.Static, color: new(255, 0, 0), random: false);
    controller.Step(poll: false, frame: false);
    var stored = SpectrumPacket.ParseEffects(FakeFirmware.Answer(1, firmware.Profiles[1]));
    Check(stored.Length == 3 && stored[0].Kind == LightingEffectKind.Static && stored[1].Kind == LightingEffectKind.RainbowWave, "firmware profile 1");
    var zone = controller.Snapshot.Zones[0];
    Check(zone.Kind == LightingEffectKind.Static && zone.Color == new LightingColor(255, 0, 0), "page sees the new keyboard");
    Check(firmware.Profiles[2].SequenceEqual(FakeFirmware.Factory[2]), "other profiles untouched");
});
Test("a slider drag writes only the last brightness", () =>
{
    var (controller, firmware, _) = Controller();
    controller.Step(poll: true, frame: false);
    controller.SetBrightness(3);
    controller.SetBrightness(5);
    controller.SetBrightness(8);
    controller.Step(poll: false, frame: false);
    Check(firmware.BrightnessWrites.SequenceEqual([8]) && controller.Snapshot.Brightness == 8, string.Join(",", firmware.BrightnessWrites));
});
Test("profile, logo and reset", () =>
{
    var (controller, firmware, _) = Controller();
    controller.Step(poll: true, frame: false);
    controller.SetProfile(3);
    controller.SetLogo(false);
    controller.Step(poll: false, frame: false);
    Check(firmware.Profile == 3 && !firmware.Logo && controller.Snapshot.Zones[0].Kind == LightingEffectKind.Ripple && !controller.Snapshot.LogoOn, "profile 3, logo off");
    controller.SetZone(LightingZone.Bar, 3, kind: LightingEffectKind.Static, color: LightingColor.White);
    controller.ResetProfile(3);
    controller.Step(poll: false, frame: false);
    Check(firmware.Profiles[3].SequenceEqual(FakeFirmware.Factory[3]) && controller.Snapshot.Zones[1].Kind == LightingEffectKind.ColorPulse, "profile 3 factory again");
});
Test("Windows Dynamic Lighting is reported and can be turned off for the laptop", () =>
{
    var (controller, _, windows) = Controller();
    windows.Value = true;
    controller.Step(poll: true, frame: false);
    Check(controller.Snapshot.WindowsControls, "controls");
    controller.TurnOffWindowsLighting();
    controller.Step(poll: false, frame: false);
    Check(windows.TurnOffs == 1 && !controller.Snapshot.WindowsControls, "turned off");
});
Test("no controller found", () =>
{
    var (controller, _, _) = Controller(found: false);
    controller.Step(poll: true, frame: false);
    var s = controller.Snapshot;
    Check(s.Ready && !s.Found && s.Zones.Length == 0, "ready, not found");
    controller.SetBrightness(3);
    controller.Step(poll: false, frame: false);
    Check(controller.Snapshot.Error is null, "commands without a device do nothing");
});
Test("a broken connection is reopened", () =>
{
    int opened = 0;
    var firmware = new FakeFirmware();
    var controller = new LightingController(() => { opened++; return new SpectrumClient(firmware, Clock, Sleep); }, new FakeWindows(),
        new LightingFiles(Path.Combine(Path.GetTempPath(), "PersonalToolsLightingTests", Guid.NewGuid().ToString("N"))), Clock);
    controller.Shown();
    controller.Step(poll: true, frame: false);
    firmware.FailGets = 1;
    controller.Step(poll: true, frame: false);
    Check(controller.Snapshot.Error is not null && opened == 1, "error shown");
    now += 1500;
    controller.Step(poll: true, frame: false);
    Check(controller.Snapshot.Error is null && opened == 2 && controller.Snapshot.Found, "reopened");
});
Test("two quick clicks on one zone both stay", () =>
{
    var (controller, firmware, _) = Controller();
    controller.Step(poll: true, frame: false);
    // Both queued before the page saw the first result: an effect, then a color from the old card.
    controller.SetZone(LightingZone.Keyboard, 1, kind: LightingEffectKind.ColorPulse);
    controller.SetZone(LightingZone.Keyboard, 1, color: new(255, 0, 0), random: false);
    controller.Step(poll: false, frame: false);
    var keyboard = controller.Snapshot.Zones[0];
    Check(keyboard.Kind == LightingEffectKind.ColorPulse && keyboard.Color == new LightingColor(255, 0, 0) && !keyboard.Random && keyboard.Speed == 2, $"{keyboard}");
    controller.SetZone(LightingZone.Keyboard, 1, speed: 3);
    controller.SetZone(LightingZone.Keyboard, 1, random: true);
    controller.Step(poll: false, frame: false);
    keyboard = controller.Snapshot.Zones[0];
    Check(keyboard.Kind == LightingEffectKind.ColorPulse && keyboard.Random && keyboard.Speed == 3, $"{keyboard}");
    Check(controller.Snapshot.Zones[1].Kind == LightingEffectKind.RainbowWave, "bar untouched");
});
Test("a change meant for another profile is dropped", () =>
{
    var (controller, firmware, _) = Controller();
    controller.Step(poll: true, frame: false);
    firmware.Profile = 4;
    controller.Step(poll: true, frame: false);
    int writes = firmware.Ops.Count(op => op is 0xCB or 0xC9);
    controller.SetZone(LightingZone.Keyboard, 1, kind: LightingEffectKind.Static);
    controller.ResetProfile(1);
    controller.Step(poll: false, frame: false);
    Check(firmware.Ops.Count(op => op is 0xCB or 0xC9) == writes, "no write to profile 4");
    Check(firmware.Profiles[4].SequenceEqual(FakeFirmware.Factory[4]), "profile 4 as it was");
});
Test("every command redraws the page, even one that changed nothing", () =>
{
    var (controller, _, _) = Controller();
    controller.Step(poll: true, frame: false);
    int changes = 0;
    controller.Changed += _ => changes++;
    controller.SetLogo(true);
    controller.Step(poll: false, frame: false);
    Check(changes == 1, $"changed {changes} times");
});
Test("a failing command does not stop the controller", () =>
{
    var (controller, firmware, windows) = Controller();
    controller.Step(poll: true, frame: false);
    windows.Throw = true;
    controller.TurnOffWindowsLighting();
    controller.SetLogo(false);
    controller.Step(poll: false, frame: false);
    Check(!firmware.Logo, "next command still ran");
});
Test("queued writes are dropped after Dispose", () =>
{
    var (controller, firmware, _) = Controller();
    controller.Step(poll: true, frame: false);
    controller.SetBrightness(2);
    controller.Dispose();
    controller.Step(poll: false, frame: false);
    Check(firmware.BrightnessWrites.Count == 0, "nothing written");
});
Test("Windows Dynamic Lighting registry values", () =>
{
    string root = $@"Software\PersonalTools\LightingTests\{Guid.NewGuid():N}";
    try
    {
        var lighting = new WindowsDynamicLighting(root);
        Check(lighting.Controls() is null, "no device key yet");
        using (var global = Registry.CurrentUser.CreateSubKey(root)) global.SetValue("AmbientLightingEnabled", 1, RegistryValueKind.DWord);
        using (var other = Registry.CurrentUser.CreateSubKey(root + @"\Devices\HID#HID_DEVICE_SYSTEM_VHF#b&1#{x}")) other.SetValue("AmbientLightingEnabled", 1, RegistryValueKind.DWord);
        using (var laptop = Registry.CurrentUser.CreateSubKey(root + @"\Devices\HID#VID_048D&PID_C903&MI_01#7&1#{x}")) laptop.SetValue("AmbientLightingEnabled", 1, RegistryValueKind.DWord);
        Check(lighting.Controls() == true, "on");
        lighting.TurnOff();
        Check(lighting.Controls() == false, "off");
        using (var laptop = Registry.CurrentUser.OpenSubKey(root + @"\Devices\HID#VID_048D&PID_C903&MI_01#7&1#{x}")) Check((int)laptop!.GetValue("AmbientLightingEnabled")! == 0, "laptop value 0");
        using (var other = Registry.CurrentUser.OpenSubKey(root + @"\Devices\HID#HID_DEVICE_SYSTEM_VHF#b&1#{x}")) Check((int)other!.GetValue("AmbientLightingEnabled")! == 1, "other devices untouched");
        using (var laptop = Registry.CurrentUser.OpenSubKey(root + @"\Devices\HID#VID_048D&PID_C903&MI_01#7&1#{x}", true)) laptop!.SetValue("AmbientLightingEnabled", 1, RegistryValueKind.DWord);
        using (var global = Registry.CurrentUser.OpenSubKey(root, true)) global!.SetValue("AmbientLightingEnabled", 0, RegistryValueKind.DWord);
        Check(lighting.Controls() == false, "global switch off");
    }
    finally { Registry.CurrentUser.DeleteSubKeyTree(root, false); }
});
Console.WriteLine($"{passed} passed");

static SpectrumClient Open() =>
    HidSpectrumTransport.Open() is { } transport ? new SpectrumClient(transport) : throw new InvalidOperationException("048D:C909 FF89:0007 not found");

sealed class FakeWindows : IDynamicLighting
{
    public bool? Value = false;
    public bool Throw;
    public int TurnOffs;
    public bool? Controls() => Value;
    public void TurnOff() { if (Throw) throw new InvalidOperationException("test"); TurnOffs++; Value = false; }
}

// The Legion 9 18IAX10 keyboard controller as measured on 2026-10-30: answers lag one request,
// an answer stays in the report for a while and then the live colors come back.
sealed class FakeFirmware : ISpectrumTransport
{
    // Bytes 4.. of each factory CC answer read from this laptop.
    public static readonly Dictionary<int, byte[]> Factory = new()
    {
        [1] = Convert.FromHexString("010101010601010202030104000500060000650100020003000400050006000700080009000A000B000C000D000E000F001000110012001300140016001700180019001A001B001C001D001E001F00200021002200260027002800290038004000420043004400450046004700480049004A004B004C004D004E004F00500051005500580059005A005B005C005D005F0068006A006D006E006F007000710072007300740075007600770079007B007C007F00800082008300870088008D008E00900092009600970098009A009B009C009D009F00A100A300A500A7000206010202020300040305000600001DF501F601F701F801F901FA01FB01FC01FD01FE01DD05000000000000000000000000000000000000000000000000000000000000000000000000"),
        [2] = Convert.FromHexString("0201010106010B0200030004000502060001158DDD820100020003000400050006000700080009000A000B000C000D000E000F001000110012001300140016001700180019001A001B001C001D001E001F00200021002200260027002800290038004000420043004400450046004700480049004A004B004C004D004E004F00500051005500580059005A005B005C005D005F0068006A006D006E006F007000710072007300740075007600770079007B007C007F00800082008300870088008D008E00900092009600970098009A009B009C009D009F00A100A300A500A700F501F601F701F801F901FA01FB01FC01FD01FE01DD05000000000000000000000000000000000000000000000000000000000000000000000000"),
        [3] = Convert.FromHexString("030101010601080203030004000501060000650100020003000400050006000700080009000A000B000C000D000E000F001000110012001300140016001700180019001A001B001C001D001E001F00200021002200260027002800290038004000420043004400450046004700480049004A004B004C004D004E004F00500051005500580059005A005B005C005D005F0068006A006D006E006F007000710072007300740075007600770079007B007C007F00800082008300870088008D008E00900092009600970098009A009B009C009D009F00A100A300A500A7000206010402020300040005010600001DF501F601F701F801F901FA01FB01FC01FD01FE01DD05000000000000000000000000000000000000000000000000000000000000000000000000"),
        [4] = Convert.FromHexString("040101010601070203030004000501060000650100020003000400050006000700080009000A000B000C000D000E000F001000110012001300140016001700180019001A001B001C001D001E001F00200021002200260027002800290038004000420043004400450046004700480049004A004B004C004D004E004F00500051005500580059005A005B005C005D005F0068006A006D006E006F007000710072007300740075007600770079007B007C007F00800082008300870088008D008E00900092009600970098009A009B009C009D009F00A100A300A500A7000206010602030300040005010600001DF501F601F701F801F901FA01FB01FC01FD01FE01DD05000000000000000000000000000000000000000000000000000000000000000000000000"),
        [5] = Convert.FromHexString("050101010601090200030004000500060000820100020003000400050006000700080009000A000B000C000D000E000F001000110012001300140016001700180019001A001B001C001D001E001F00200021002200260027002800290038004000420043004400450046004700480049004A004B004C004D004E004F00500051005500580059005A005B005C005D005F0068006A006D006E006F007000710072007300740075007600770079007B007C007F00800082008300870088008D008E00900092009600970098009A009B009C009D009F00A100A300A500A700F501F601F701F801F901FA01FB01FC01FD01FE01DD05000000000000000000000000000000000000000000000000000000000000000000000000"),
        [6] = Convert.FromHexString("0601010106010A0200030004000500060000820100020003000400050006000700080009000A000B000C000D000E000F001000110012001300140016001700180019001A001B001C001D001E001F00200021002200260027002800290038004000420043004400450046004700480049004A004B004C004D004E004F00500051005500580059005A005B005C005D005F0068006A006D006E006F007000710072007300740075007600770079007B007C007F00800082008300870088008D008E00900092009600970098009A009B009C009D009F00A100A300A500A700F501F601F701F801F901FA01FB01FC01FD01FE01DD05000000000000000000000000000000000000000000000000000000000000000000000000"),
    };
    // The key matrix (C4/C5) read from this laptop.
    private static readonly string[] Matrix =
    [
        "000 000 3E9 3F3 3EA 3F4 3EB 3F5 3EC 3F6 3ED 3EE 3EF 3F7 3F0 3F8 3F1 3F9 3F2 3FA 000 000",
        "000 001 002 003 004 005 006 007 008 009 00A 00B 00C 00D 00E 00F 010 011 012 013 014 000",
        "000 016 017 018 019 01A 01B 01C 000 01D 01E 01F 020 021 022 038 038 026 027 028 029 000",
        "000 040 042 000 043 044 045 046 047 048 049 04A 04B 000 04C 04D 04E 04F 050 051 068 000",
        "000 055 055 06D 06E 058 059 05A 071 000 072 05B 05C 05D 05F 077 077 079 07B 07C 068 000",
        "1F5 06A 06A 082 083 000 06F 070 087 088 073 074 075 000 076 08D 08D 08E 090 092 0A7 1FE",
        "1F5 07F 080 096 097 098 098 098 098 098 098 09A 09B 000 000 09D 000 0A3 0A3 0A5 0A7 1FE",
        "1F6 000 000 000 000 000 000 000 000 000 000 000 000 000 09C 09F 000 0A1 000 000 000 1FD",
        "1F6 1F7 1F7 1F7 1F7 1F8 1F8 1F8 1F9 1F9 1F9 1FA 1FA 1FA 1FB 1FB 1FB 1FC 1FC 1FC 1FC 1FD",
    ];
    public static ushort[][] Rows() => Matrix.Select(r => r.Split(' ').Select(c => Convert.ToUInt16(c, 16)).ToArray()).ToArray();
    public static SpectrumLayout Layout() => new(Rows(), [SpectrumCodes.Logo]);
    public static byte[] Answer(int profile, byte[] stored)
    {
        var answer = new byte[960];
        answer[0] = 7; answer[1] = 0xCC; answer[2] = 0xC0; answer[3] = 3;
        stored.CopyTo(answer, 4);
        answer[4] = (byte)profile;
        return answer;
    }

    public int Brightness = 9, Profile = 1;
    public bool Logo = true;
    public readonly Dictionary<int, byte[]> Profiles = Factory.ToDictionary(p => p.Key, p => p.Value.ToArray());
    public readonly List<int> BrightnessWrites = [];
    public readonly List<byte> Ops = [];
    public int Lag = 1, Hold = 15, DropNext, SetWhileBusy, Requests, FailGets;
    private byte[]? previous, current;
    private int reads;

    public void Dispose() { }
    public bool SetFeature(byte[] report)
    {
        Requests++;
        Ops.Add(report[1]);
        if (current is not null && reads < Lag + Hold) SetWhileBusy++;
        if (DropNext > 0) { DropNext--; return true; }
        var answer = Handle(report);
        if (answer is null) { previous = current; current = null; reads = 0; return true; }
        previous = current ?? previous;
        current = answer;
        reads = 0;
        return true;
    }
    public bool GetFeature(byte[] report)
    {
        if (FailGets > 0) { FailGets--; return false; }
        byte[] source;
        if (current is null) source = State();
        else if (reads < Lag) source = previous ?? State();
        else if (reads < Lag + Hold) source = current;
        else { previous = current; current = null; source = State(); }
        reads++;
        source.CopyTo(report, 0);
        return true;
    }

    private byte[]? Handle(byte[] r)
    {
        byte op = r[1];
        var rows = Rows();
        switch (op)
        {
            case 0xC4: return Small(op, r[4], r[4] == 7 ? (byte)rows.Length : (byte)1, r[4] == 7 ? (byte)rows[0].Length : (byte)13);
            case 0xC5:
            {
                var a = Small(op, r[4], r[5]);
                a[2] = 0x41;
                ushort[] cells = r[4] == 7 ? rows[r[5]] : [SpectrumCodes.Logo];
                for (int c = 0; c < cells.Length; c++) { a[6 + c * 3] = (byte)c; BitConverter.GetBytes(cells[c]).CopyTo(a, 7 + c * 3); }
                return a;
            }
            case 0xCA: return Small(op, (byte)Profile);
            case 0xCD: return Small(op, (byte)Brightness);
            case 0xA5: return Small(op, Logo ? (byte)1 : (byte)0);
            case 0xCC: return Answer(r[4], Profiles[r[4]]);
            case 0xCE: Brightness = r[4]; BrightnessWrites.Add(r[4]); return null;
            case 0xA6: Logo = r[4] == 1; return null;
            case 0xC8: Profile = r[4]; return null;
            case 0xC9: Profiles[r[4]] = Factory[r[4]].ToArray(); return null;
            case 0xCB:
            {
                int length = BitConverter.ToUInt16(r, 2);
                var stored = new byte[length];
                Array.Copy(r, 4, stored, 0, length);
                Profiles[r[4]] = stored;
                return null;
            }
            default: return Small(op);
        }
    }
    private static byte[] Small(byte op, params byte[] values)
    {
        var a = new byte[960];
        a[0] = 7; a[1] = op; a[2] = 1;
        values.CopyTo(a, 4);
        return a;
    }
    // Every code of the matrix plus the logo, static colors from the active profile, others grey.
    private byte[] State()
    {
        var effects = SpectrumPacket.ParseEffects(Answer(Profile, Profiles[Profile]));
        var codes = Rows().SelectMany(r => r).Where(c => c != 0).Distinct().Append(SpectrumCodes.Logo).Distinct().ToArray();
        var report = new byte[960];
        report[0] = 7; report[1] = 3;
        BitConverter.GetBytes((ushort)(codes.Length * 5)).CopyTo(report, 2);
        for (int i = 0; i < codes.Length; i++)
        {
            int at = 4 + i * 5;
            BitConverter.GetBytes(codes[i]).CopyTo(report, at);
            var effect = effects.FirstOrDefault(e => e.Keys.Contains(codes[i]));
            var color = SpectrumCodes.ZoneOf(codes[i]) is null ? LightingColor.Black
                : codes[i] == SpectrumCodes.Logo && !Logo ? LightingColor.Black
                : effect?.ColorMode == LightingColorMode.List ? effect.Colors[0] : new LightingColor(0x80, 0x80, 0x80);
            report[at + 2] = (byte)(color.R * Brightness / 9); report[at + 3] = (byte)(color.G * Brightness / 9); report[at + 4] = (byte)(color.B * Brightness / 9);
        }
        return report;
    }
}
