using System;
using System.Collections.Generic;
using System.Linq;

namespace BladeAndSorcery.Multiplayer.Core;

// A travel proposal is accepted before any participant unloads its native map.
public sealed class TravelAgreement
{
    private readonly HashSet<ushort> waiting = new HashSet<ushort>();
    public uint Id { get; private set; }
    public bool Active { get; private set; }
    public string Failure { get; private set; } = "";
    public double Deadline { get; private set; }
    public bool Ready => Active && waiting.Count == 0 && Failure.Length == 0;
    public bool Begin(uint id, IEnumerable<ushort> peers, double now, double timeout = 30)
    {
        if(Active || id==0 || double.IsNaN(now) || double.IsInfinity(now) || timeout<=0) return false;
        Id=id;Deadline=now+timeout;Failure="";waiting.Clear();
        foreach(var peer in peers.Where(p=>p!=1)) waiting.Add(peer);
        Active=true;return true;
    }
    public bool Reply(uint id, ushort peer, bool accepted, string reason)
    {
        if(!Active || id!=Id || !waiting.Remove(peer)) return false;
        if(!accepted && Failure.Length==0) Failure="Player "+peer+": "+reason;
        return true;
    }
    public void Remove(ushort peer)=>waiting.Remove(peer);
    public void Add(ushort peer){if(Active && peer!=1)waiting.Add(peer);}
    public bool TimedOut(double now)=>Active && !Ready && now>=Deadline;
    public void End(){Active=false;waiting.Clear();}
}
