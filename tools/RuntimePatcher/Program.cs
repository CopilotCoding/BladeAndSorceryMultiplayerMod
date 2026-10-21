using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Only the isolated, disposable runtime is patched. Never accept the installed game path.
var root = Path.GetFullPath(args.Length == 1 ? args[0] : throw new ArgumentException("Expected isolated runtime directory"));
if (!File.Exists(Path.Combine(root, ".basmp-test-runtime")))
    throw new InvalidOperationException("Refusing to patch a directory without the test-runtime marker.");
var path = Path.Combine(root, "BladeAndSorcery_Data", "Managed", "ThunderRoad.dll");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadWrite = false, AssemblyResolver = resolver });
var method = assembly.MainModule.Types.Single(t => t.FullName == "ThunderRoad.PlatformWindows")
    .Methods.Single(m => m.Name == "TryGetSavePath");
// Reference the game's Mono core library, not this utility's System.Private.CoreLib.
var environmentType = new TypeReference("System", "Environment", assembly.MainModule, assembly.MainModule.TypeSystem.CoreLibrary);
var getEnvironment = new MethodReference("GetEnvironmentVariable", assembly.MainModule.TypeSystem.String, environmentType) { HasThis = false };
getEnvironment.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.String));
if (!method.Body.Instructions.Any(i => Equals(i.Operand, "BASMP_TEST_SAVE_ROOT")))
{
    var variable = new VariableDefinition(assembly.MainModule.TypeSystem.String);
    method.Body.Variables.Add(variable); method.Body.InitLocals = true;
    var il = method.Body.GetILProcessor(); var original = method.Body.Instructions[0];
    foreach (var instruction in new[] {
        il.Create(OpCodes.Ldstr, "BASMP_TEST_SAVE_ROOT"), il.Create(OpCodes.Call, getEnvironment),
        il.Create(OpCodes.Stloc, variable), il.Create(OpCodes.Ldloc, variable), il.Create(OpCodes.Brfalse, original),
        il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Ldloc, variable), il.Create(OpCodes.Stind_Ref),
        il.Create(OpCodes.Ldc_I4_1), il.Create(OpCodes.Ret)
    }) il.InsertBefore(original, instruction);
}
var logMethod = assembly.MainModule.Types.Single(t => t.FullName == "ThunderRoad.FileManager")
    .Methods.Single(m => m.Name == "GetFullPath");
if (!logMethod.Body.Instructions.Any(i => Equals(i.Operand, "BASMP_TEST_SAVE_ROOT")))
{
    var variable = new VariableDefinition(assembly.MainModule.TypeSystem.String);
    logMethod.Body.Variables.Add(variable); logMethod.Body.InitLocals = true;
    var il = logMethod.Body.GetILProcessor(); var original = logMethod.Body.Instructions[0];
    var pathType = new TypeReference("System.IO", "Path", assembly.MainModule, assembly.MainModule.TypeSystem.CoreLibrary);
    var combine = new MethodReference("Combine", assembly.MainModule.TypeSystem.String, pathType) { HasThis = false };
    for (int i = 0; i < 3; i++) combine.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.String));
    foreach (var instruction in new[] {
        il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Ldc_I4_2), il.Create(OpCodes.Bne_Un, original),
        il.Create(OpCodes.Ldstr, "BASMP_TEST_SAVE_ROOT"), il.Create(OpCodes.Call, getEnvironment),
        il.Create(OpCodes.Stloc, variable), il.Create(OpCodes.Ldloc, variable), il.Create(OpCodes.Brfalse, original),
        il.Create(OpCodes.Ldloc, variable), il.Create(OpCodes.Ldstr, "logs"), il.Create(OpCodes.Ldarg_2),
        il.Create(OpCodes.Call, combine), il.Create(OpCodes.Ret)
    }) il.InsertBefore(original, instruction);
}
var temporary = path + ".basmp-new";
assembly.Write(temporary);
assembly.Dispose();
File.Move(temporary, path, true);
Console.WriteLine("Verified save and log isolation in the disposable runtime only.");
