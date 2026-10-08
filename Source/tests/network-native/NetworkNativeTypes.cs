using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

// Only Unity's external random API is simulated. The native Solasta random,
// state restore/record, raw dice, and replicated rest coroutine IL are copied.
namespace UnityEngine
{
    public class Object { public static bool operator ==(Object a,Object b){return ReferenceEquals(a,b);} public static bool operator !=(Object a,Object b){return !ReferenceEquals(a,b);} public override bool Equals(object o){return ReferenceEquals(this,o);} public override int GetHashCode(){return base.GetHashCode();} }
    public class MonoBehaviour:Object { }
    // The production scheduler names Unity's fixed time step; fixtures that
    // model game ticks bind their own answer instead (it is never read here).
    public static class Time { public static bool inFixedTimeStep { get { throw new System.NotSupportedException("Unity fixed time step outside the player"); } } }
    public static class Random
    {
        public struct State { private int s0,s1,s2,s3; public uint word {get{return unchecked((uint)s0);}set{s0=unchecked((int)value);}} }
        static State current; public static int Calls;
        public static State state { get{return current;}set{current=value;} }
        public static void InitState(int seed){current.word=(uint)seed;}
        static uint Next(){Calls++;current.word=unchecked(current.word*1664525u+1013904223u);return current.word;}
        public static int Range(int min,int max){return min+(int)(Next()%(uint)(max-min));}
        public static float Range(float min,float max){return min+(max-min)*value;}
        public static float value {get{return (Next()>>8)*(1.0f/16777216);}}
    }
    public static class Mathf
    {
        public static int Max(int a,int b){return Math.Max(a,b);}public static int Min(int a,int b){return Math.Min(a,b);}
        public static float Max(float a,float b){return Math.Max(a,b);}public static float Abs(float a){return Math.Abs(a);}
        public static float Lerp(float a,float b,float t){return a+(b-a)*Math.Max(0,Math.Min(1,t));}public static float Pow(float a,float b){return (float)Math.Pow(a,b);}
    }
}
public interface IGameService {Game Game{get;}}
public class TestGameService:IGameService {public Game Game{get;set;}}
public static class ServiceRepository {public static TestGameService Current;public static T GetService<T>(){return (T)(object)Current;}}
public class Game:UnityEngine.MonoBehaviour
{
    int randomSeed;UnityEngine.Random.State randomState;
    public Game(int seed){randomSeed=seed;UnityEngine.Random.InitState(seed);randomState=UnityEngine.Random.state;}
    public int RandomSeed{get{return randomSeed;}}
    public UnityEngine.Random.State RandomState{get{return randomState;}}
    [MethodImpl(MethodImplOptions.NoInlining)] public void RecordDeterministicRandomState(){throw new Exception("Native Game.Record IL missing");}
    [MethodImpl(MethodImplOptions.NoInlining)] public void ResetDeterministicRandomState(){throw new Exception("Native Game.Reset IL missing");}
}
namespace TA
{
    public class Coroutine {private Stack<IEnumerator> enumerators=new Stack<IEnumerator>();public bool IsFinished{get;protected set;}public bool IsSuspended{get;protected set;}public Exception LastException{get;private set;}public void Start(IEnumerator e){enumerators.Push(e);IsFinished=false;}[MethodImpl(MethodImplOptions.NoInlining)]public virtual void Run(){throw new Exception("Native TA.Run IL missing");}protected IEnumerator EnumerableRun(){while(!IsFinished){Run();if(!IsFinished)yield return null;}}}

    public static class DeterministicRandom
    {
        public enum DeterministicLockFlag {None,Cursor,AI,Bestiary}
        static HashSet<DeterministicLockFlag> lockFlags=new HashSet<DeterministicLockFlag>();
        public static HashSet<DeterministicLockFlag> LockFlags{get{return lockFlags;}}
        public static UnityEngine.Random.State state{get{throw new Exception("Native state getter IL missing");}}
        public static float value{get{throw new Exception("Native value getter IL missing");}}
        public static int Range(int min,int max){throw new Exception("Native int Range IL missing");}
        public static float Range(float min,float max){throw new Exception("Native float Range IL missing");}
    }
}
public class DifficultyConfiguration {public float MaxCurveOffset{get;set;}=.4f;public float OffsetIgnoredThreshold{get;set;}=.1f;public float MaxCurvePower{get;set;}=2;}
public static class GameConfiguration {public static DifficultyConfiguration Difficulty{get;set;}=new DifficultyConfiguration();}
public static class RuleDefinitions
{
    public enum DieType {D1,D2,D3,D4,D6,D8,D10,D12,D20}
    public enum AdvantageType {None,Advantage,Disadvantage}
    public static int[] DiceMaxValue={1,2,3,4,6,8,10,12,20};public static int[] DiceMinValue={1,1,1,1,1,1,1,1,1};
    [MethodImpl(MethodImplOptions.NoInlining)]public static int RollDie(DieType diceType,AdvantageType advantageType,out int firstRoll,out int secondRoll,float rollAlterationScore=0){firstRoll=secondRoll=0;throw new Exception("Native raw RollDie IL missing");}
    [MethodImpl(MethodImplOptions.NoInlining)]public static int RollKarmicDie(DieType diceType,float rollAlterationScore){throw new Exception("Native karmic IL missing");}
    [MethodImpl(MethodImplOptions.NoInlining)]public static int RollStaticDiceAndSum(int diceNumber,DieType diceType,List<int> rolledValues=null){throw new Exception("Native static sum IL missing");}
}
public class RulesetEntity
{
    public static Dictionary<ulong,RulesetEntity> Entities=new Dictionary<ulong,RulesetEntity>();public ulong Guid;
    public static bool TryGetEntity<T>(ulong guid,out T entity) where T:RulesetEntity{RulesetEntity found;entity=Entities.TryGetValue(guid,out found)?found as T:null;return entity!=null;}
}
public class RulesetCharacterHero:RulesetEntity
{
    public int Count,HitPoints;
    public void RollHitDie(){int first,second;Count++;HitPoints+=RuleDefinitions.RollDie(RuleDefinitions.DieType.D8,RuleDefinitions.AdvantageType.None,out first,out second);}
}
public static class RestDefinitions {public delegate void RestActionExecutedHandler();}
public class LocalCommandManager
{
    [MethodImpl(MethodImplOptions.NoInlining)]public void RollHitDie(RulesetCharacterHero hero,RestDefinitions.RestActionExecutedHandler restActionExecuted){throw new Exception("Native replicated command IL missing");}
}
namespace Photon.Pun {public struct PhotonMessageInfo {public int Sender;}}
public class NetworkingManager
{
    LocalCommandManager localCommandManager=new LocalCommandManager();
    Dictionary<ulong,RestDefinitions.RestActionExecutedHandler> localRestCallbacks=new Dictionary<ulong,RestDefinitions.RestActionExecutedHandler>();
    public int CallbackCount;
    T RecoverLocalCallback<T>(ulong callbackId,Photon.Pun.PhotonMessageInfo info,Dictionary<ulong,T> callbacks){T callback;return callbacks.TryGetValue(callbackId,out callback)?callback:default(T);}
    [MethodImpl(MethodImplOptions.NoInlining)]IEnumerator RollHitDieAsync(long callbackId,long characterId,Photon.Pun.PhotonMessageInfo info){throw new Exception("Native replicated iterator IL missing");}
    public IEnumerator InvokeRest(ulong heroGuid,bool localCallback){if(localCallback)localRestCallbacks[1]=()=>CallbackCount++;return RollHitDieAsync(1,(long)heroGuid,new Photon.Pun.PhotonMessageInfo());}
}

public static class Trace {public static void LogException(Exception exception){}}
