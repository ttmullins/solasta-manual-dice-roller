using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

class InventoryNetwork
{
    static string Hash(PEReader pe,Mono.Cecil.MethodDefinition method){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(pe.GetMethodBody(method.RVA).GetILBytes().ToArray())).Replace("-","").ToLowerInvariant();}
    static System.Collections.Generic.IEnumerable<Mono.Cecil.TypeDefinition> Types(System.Collections.Generic.IEnumerable<Mono.Cecil.TypeDefinition> types){foreach(var type in types){yield return type;foreach(var nested in Types(type.NestedTypes))yield return nested;}}
    static void Main(string[] args)
    {
        using(var game=Mono.Cecil.ModuleDefinition.ReadModule(args[0]))using(var stream=File.OpenRead(args[0]))using(var pe=new PEReader(stream))
        {
            Console.WriteLine("TA.Run fingerprint: "+Hash(pe,game.GetType("TA.Coroutine").Methods.Single(m=>m.Name=="Run")));
            Console.WriteLine("Banter.UpdateImpl fingerprint: "+Hash(pe,game.GetType("GameLocationBanterManager").Methods.Single(m=>m.Name=="UpdateImpl")));
            foreach(var rng in game.GetType("TA.DeterministicRandom").Methods.Where(m=>m.Name=="Range"||m.Name=="get_value"))Console.WriteLine("RNG body fingerprint: "+rng.FullName+" "+Hash(pe,rng));
            var manager=game.GetType("NetworkingManager");
            var rest=manager.Methods.Single(m=>m.Name=="RollHitDieAsync");
            var routine=((MethodReference)rest.Body.Instructions.Single(i=>i.OpCode==OpCodes.Newobj).Operand).DeclaringType.Resolve();
            var move=routine.Methods.Single(m=>m.Name=="MoveNext");
            var draw=move.Body.Instructions.Single(i=>i.Operand is MethodReference r&&r.DeclaringType.FullName=="LocalCommandManager"&&r.Name=="RollHitDie");
            Console.WriteLine("Rest await owner: "+routine.FullName.Replace('/','+'));
            Console.WriteLine("Rest MoveNext fingerprint: "+Hash(pe,move));
            Console.WriteLine("Rest native call offset: 0x"+draw.Offset.ToString("x4"));
            foreach(var name in new[]{"ExecuteAction","ExecuteActionChain","RollHitDie","ProcessReactionRequest"})
            {
                var method=manager.Methods.Single(m=>m.Name==name);
                Console.WriteLine("Replicated command: "+method.FullName);
                foreach(var i in method.Body.Instructions.Where(i=>i.OpCode==OpCodes.Ldstr||i.Operand is MethodReference m&&m.Name=="RPC"))Console.WriteLine("  "+i);
            }
            var action=game.GetType("CharacterActionParams");
            if(action.Fields.Any(f=>f.Name=="localId"||f.Name=="guid"))throw new Exception("Action identity assumption changed");
            Console.WriteLine("PASS CharacterActionParams has no persistent LocalId/Guid");
            foreach(var name in new[]{"get_LocalTick","get_CommandTick"})Console.WriteLine("Network tick selector: "+game.GetType("NetworkTick").Methods.Single(m=>m.Name==name).FullName);
            Console.WriteLine("Owner resolver: "+game.GetType("IPlayerControllerService").Methods.Single(m=>m.Name=="GetPlayerControllingHero").FullName);
            foreach(var method in game.GetType("TA.DeterministicRandom").Methods.Where(m=>m.Name=="Range"||m.Name=="get_value"))
            {
                if(!method.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.Name=="ResetDeterministicRandomState")||!method.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.Name=="RecordDeterministicRandomState"))throw new Exception("Native RNG restore/record assumption changed");
                Console.WriteLine("PASS native RNG restore/record: "+method.FullName);
            }
            foreach(var type in Types(game.Types))foreach(var method in type.Methods.Where(m=>m.HasBody))foreach(var instruction in method.Body.Instructions)
                if(instruction.Operand is MethodReference rng&&rng.DeclaringType.FullName=="TA.DeterministicRandom"&&(rng.Name=="Range"||rng.Name=="get_value"))
                    Console.WriteLine("DETERMINISTIC_RNG_CALLER "+method.FullName);
        }
    }
}
