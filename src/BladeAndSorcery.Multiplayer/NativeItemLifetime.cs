using ThunderRoad;

namespace BladeAndSorcery.Multiplayer;

internal sealed partial class ReplicatedEntity
{
    private bool itemLifetimeProtected;
    internal void RefreshItemLifetime()
    {
        if(Item==null) return;
        // Native cleanup runs on the simulation owner. Replicas must wait for
        // its reliable removal instead of independently cleaning shared objects.
        if(!Owned)
        {
            if(!itemLifetimeProtected){oldDisallowDespawn=Item.DisallowDespawn;itemLifetimeProtected=true;}
            Item.DisallowDespawn=true;
        }
        else if(itemLifetimeProtected)
        {
            Item.DisallowDespawn=oldDisallowDespawn;itemLifetimeProtected=false;
        }
        // The native owner may change this flag while nocking, firing or
        // storing an item. Do not overwrite those live native decisions.
    }
}
