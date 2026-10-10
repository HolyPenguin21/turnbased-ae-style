namespace UnityEditor.Build { public interface IPreprocessBuildWithReport { int callbackOrder { get; } void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report); } public class BuildFailedException : System.Exception { public BuildFailedException(string message):base(message){} } }
namespace UnityEditor.Build.Reporting { public class BuildReport {} }
