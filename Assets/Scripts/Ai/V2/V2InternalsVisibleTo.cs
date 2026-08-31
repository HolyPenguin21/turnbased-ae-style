// Strategy V2 keeps its stage classes (MissionLayer, ResourceAllocator, MissionContinuityLayer,
// ...) `internal` — only Pipeline.RunTurn orchestrates them. The standalone acceptance harnesses
// under Tools/ (each build-order step ships one) need to drive the stage under test directly, so
// they are named here. This widens visibility to those specific test assemblies only, nothing else.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("commitment-sim")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("mission-selection-sim")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("housekeeping-sim")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("recon-cooldown-sim")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("recon-throughput-sim")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("capability-quality-sim")]
