namespace Ali.Core.Permissions;

public enum PermissionRisk
{
    ReadOnly = 0,
    LocalBuild = 10,
    LocalTest = 20,
    FileWrite = 30,
    NetworkEgress = 40,
    PackageRestore = 50,
    ScriptExecution = 60,
    CalendarWrite = 70,
    SensitiveFileRead = 80,
    ModelSwitch = 90,
    LanPairing = 100,
    AdminSystemAction = 110,
    Destructive = 120
}
