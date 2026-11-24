using Felix.WindowSetup.Core.Tests;

await WindowSetupPreflightTests.Run();
SourceImageTests.Run();
TargetModelTests.Run();
BootMenuTests.Run();
await SourceSearchTests.Run();
Console.WriteLine("Window Setup Core tests passed.");
