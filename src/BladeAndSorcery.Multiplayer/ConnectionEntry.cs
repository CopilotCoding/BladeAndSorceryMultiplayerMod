using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using ThunderRoad;
using UnityEngine;

namespace BladeAndSorcery.Multiplayer;

public sealed partial class MultiplayerRuntime
{
    internal Keyboard? ConnectionKeyboard { get; private set; }
    internal string ConnectionEditField { get; private set; } = "";
    internal bool ConnectionKeyboardLoading { get; private set; }
    private int keyboardGeneration;
    private Keyboard? pendingConnectionKeyboard;
    private string addressInput = "127.0.0.1", portInput = "9050";
    private string connectionNotice = "";

    private void InitializeConnectionFields()
    {
        addressInput = Configuration.Address;
        portInput = Configuration.Port.ToString(CultureInfo.InvariantCulture);
    }
    internal bool SetConnectionEndpoint(string address, string port, bool save)
    {
        address=(address ?? "").Trim();
        port=(port ?? "").Trim();
        if (Session!=null) return ConnectionError("Leave the current session before changing connection settings.");
        if (address.Length==0 || address.Length>128 || address.Any(c=>char.IsWhiteSpace(c) || char.IsControl(c)) || address.Contains("/") || address.Contains("\\"))
            return ConnectionError("Enter a host IP or hostname without a URL or spaces.");
        if(address.Contains(":"))
        {
            address=address.Trim('[',']');
            if(!IPAddress.TryParse(address,out _)) return ConnectionError("Enter only the IP here. Set its port with Edit UDP port.");
        }
        if (!int.TryParse(port,NumberStyles.None,CultureInfo.InvariantCulture,out int number) || number<1024 || number>65535)
            return ConnectionError("UDP port must be a number from 1024 to 65535.");
        var beforeAddress=Configuration.Address;int beforePort=Configuration.Port;
        Configuration.Address=address;Configuration.Port=number;
        if (save && !SaveConnectionSettings()) { Configuration.Address=beforeAddress;Configuration.Port=beforePort;return false; }
        InitializeConnectionFields();return true;
    }
    private bool ConnectionError(string error) { connectionNotice=error;WriteLog(error);RefreshNativeStatus();return false; }
    private bool SaveConnectionSettings()
    {
        try { File.WriteAllText(configPath,JsonConvert.SerializeObject(Configuration,Formatting.Indented));connectionNotice="Connection settings saved.";RefreshNativeStatus();return true; }
        catch(Exception error) when(error is IOException || error is UnauthorizedAccessException) { return ConnectionError("Could not save connection settings: "+error.Message); }
    }
    private bool ApplyDesktopEndpoint() => SetConnectionEndpoint(addressInput,portInput,false);

    internal bool PasteConnectionEndpoint()
    {
        if(Session!=null) return ConnectionError("Leave the current session before changing connection settings.");
        string pasted;
        try { pasted=(GUIUtility.systemCopyBuffer ?? "").Trim(); }
        catch(Exception) { return ConnectionError("Could not read the desktop clipboard. Use Edit host IP / hostname."); }
        if(pasted.Length==0 || pasted.Length>196) return ConnectionError("Copy a host IP or hostname on the desktop, then press Paste IP from clipboard.");
        string address=pasted,port=Configuration.Port.ToString(CultureInfo.InvariantCulture);
        // Accept an optional port without mistaking an IPv6 address for host:port.
        if(pasted.StartsWith("[",StringComparison.Ordinal))
        {
            int close=pasted.IndexOf(']');
            if(close<0) return ConnectionError("Copy an IP or hostname, optionally followed by :port.");
            address=pasted.Substring(1,close-1);
            if(close+1<pasted.Length)
            {
                if(pasted[close+1]!=':') return ConnectionError("Copy an IP or hostname, optionally followed by :port.");
                port=pasted.Substring(close+2);
            }
        }
        else if(pasted.Count(c=>c==':')==1)
        {
            int separator=pasted.IndexOf(':');address=pasted.Substring(0,separator);port=pasted.Substring(separator+1);
        }
        if(!SetConnectionEndpoint(address,port,true)) return false;
        CloseConnectionKeyboard();
        connectionNotice="Pasted server "+Configuration.Address+":"+Configuration.Port+". Press Join Direct IP to connect.";
        RefreshNativeStatus();return true;
    }

    internal void EditConnection(string field)
    {
        if (Session!=null) { ConnectionError("Leave the current session before editing connection settings.");return; }
        if (field!="address" && field!="port" && field!="name" && field!="password") throw new ArgumentOutOfRangeException(nameof(field));
        var menu=UIPlayerMenu.instance;
        if(menu==null || !menu.IsShownToPlayer || menu.contentArea==null) { ConnectionError("Open the in-game Mods menu before editing connection settings.");return; }
        CloseConnectionKeyboard();
        if(!Catalog.TryGetData<KeyboardData>("QwertyLatin",out var source,false)) { ConnectionError("The game's native keyboard is unavailable.");return; }
        var data=(KeyboardData)source.Clone();data.PasswordMode=field=="password";
        ConnectionEditField=field;ConnectionKeyboardLoading=true;
        int generation=keyboardGeneration;
        data.SpawnAsync(keyboard=>
        {
            if(this==null || generation!=keyboardGeneration || menu==null || !menu.IsShownToPlayer)
            { if(keyboard!=null) keyboard.Despawn();return; }
            if(keyboard==null) { ConnectionKeyboardLoading=false;ConnectionEditField="";ConnectionError("Could not load the game's native keyboard.");return; }
            pendingConnectionKeyboard=keyboard;
            StartCoroutine(FinishLoading());
            IEnumerator FinishLoading()
            {
            // Native VRKB initializes its input field in Start, after the
            // Addressables callback. Setting text before that loses the value.
            yield return null;
            if(keyboard==null || generation!=keyboardGeneration || menu==null || !menu.IsShownToPlayer) yield break;
            pendingConnectionKeyboard=null;ConnectionKeyboard=keyboard;ConnectionKeyboardLoading=false;
            keyboard.OnConfirm.AddListener(ConfirmConnectionEntry);
            keyboard.OnCancel.AddListener(_=>{connectionNotice="Edit cancelled. Previous settings kept.";CloseConnectionKeyboard();RefreshNativeStatus();});
            string text=field=="address" ? Configuration.Address : field=="port" ? Configuration.Port.ToString(CultureInfo.InvariantCulture) : field=="name" ? Configuration.PlayerName : Configuration.Password;
            string title=field=="address" ? "Host IP or hostname" : field=="port" ? "UDP port (same as the host)" : field=="name" ? "Your player name" : "Server password (blank for none)";
            var anchor=menu.contentArea;
            // Use the menu's uniformly scaled root, outside its scrolling mask.
            // Parenting to contentArea distorts the keys and clips the input text.
            keyboard.Show(anchor.position,anchor.rotation,menu.transform,title);
            keyboard.SetTypedText(text);
            FitConnectionKeyboard(keyboard,anchor);
            connectionNotice="Editing "+title+". Confirm saves; Cancel keeps the previous value.";RefreshNativeStatus();
            }
        },1f,"Connection settings",position:menu.contentArea.position,rotation:menu.contentArea.rotation);
    }
    private static void FitConnectionKeyboard(Keyboard keyboard,Transform anchor)
    {
        // The native keyboard's keys lie on X/Z; the menu lies on X/Y.
        keyboard.transform.rotation=anchor.rotation*Quaternion.Euler(-90,0,0);
        var keys=keyboard.GetComponentsInChildren<KeyButton>().Select(k=>k.GetComponent<BoxCollider>()).Where(c=>c!=null).ToArray();
        if(keys.Length==0) return;
        Vector3 right=anchor.right,up=anchor.up;
        (float minX,float maxX,float minY,float maxY) Extents()
        {
            float minX=float.PositiveInfinity,maxX=float.NegativeInfinity,minY=minX,maxY=maxX;
            foreach(var key in keys) for(int x=-1;x<=1;x+=2) for(int y=-1;y<=1;y+=2) for(int z=-1;z<=1;z+=2)
            {
                var p=key.transform.TransformPoint(key.center+Vector3.Scale(key.size,new Vector3(x,y,z))*.5f);
                float px=Vector3.Dot(p,right),py=Vector3.Dot(p,up);
                minX=Mathf.Min(minX,px);maxX=Mathf.Max(maxX,px);minY=Mathf.Min(minY,py);maxY=Mathf.Max(maxY,py);
            }
            return(minX,maxX,minY,maxY);
        }
        var corners=new Vector3[4];float menuWidth=.65f;Vector3 menuCenter=anchor.position;
        if(anchor is RectTransform rect) { rect.GetWorldCorners(corners);menuWidth=Vector3.Distance(corners[0],corners[3]);menuCenter=rect.TransformPoint(rect.rect.center); }
        var before=Extents();float width=before.maxX-before.minX;
        if(width<.00001f) return;
        keyboard.transform.localScale*=Mathf.Clamp(menuWidth*.95f,.35f,.85f)/width;
        var after=Extents();
        var center=right*((after.minX+after.maxX)*.5f)+up*((after.minY+after.maxY)*.5f)+anchor.forward*Vector3.Dot(keyboard.transform.position,anchor.forward);
        var destination=menuCenter-anchor.forward*.04f;
        keyboard.transform.position+=destination-center;
    }
    private void ConfirmConnectionEntry(string text)
    {
        if(Session!=null || ConnectionKeyboard==null) { CloseConnectionKeyboard();return; }
        bool accepted;
        if(ConnectionEditField=="address") accepted=SetConnectionEndpoint(text,Configuration.Port.ToString(CultureInfo.InvariantCulture),true);
        else if(ConnectionEditField=="port") accepted=SetConnectionEndpoint(Configuration.Address,text,true);
        else
        {
            bool password=ConnectionEditField=="password";
            string value=password ? text : text.Trim();
            if(value.Length>(password ? 64 : 32) || (!password && value.Length==0) || value.Any(char.IsControl)) { ConnectionError(password ? "Use a password of at most 64 characters." : "Use a player name of 1 to 32 characters.");return; }
            string previous=password ? Configuration.Password : Configuration.PlayerName;
            if(password) Configuration.Password=value;else Configuration.PlayerName=value;
            accepted=SaveConnectionSettings();
            if(!accepted) { if(password) Configuration.Password=previous;else Configuration.PlayerName=previous; }
        }
        if(accepted) CloseConnectionKeyboard();
        else ConnectionKeyboard?.SetPlaceholderText(connectionNotice);
    }
    internal void CloseConnectionKeyboard()
    {
        keyboardGeneration++;ConnectionKeyboardLoading=false;ConnectionEditField="";
        var keyboard=ConnectionKeyboard;ConnectionKeyboard=null;
        if(keyboard!=null) keyboard.Despawn();
        var pending=pendingConnectionKeyboard;pendingConnectionKeyboard=null;
        if(pending!=null && pending!=keyboard) pending.Despawn();
    }
    private void TickConnectionKeyboard()
    {
        if((ConnectionKeyboard!=null || ConnectionKeyboardLoading) && (UIPlayerMenu.instance==null || !UIPlayerMenu.instance.IsShownToPlayer || Session!=null || LevelManager.isLoadingLocked))
        { connectionNotice="Edit cancelled. Previous settings kept.";CloseConnectionKeyboard(); }
    }
    [ModOption("Edit host IP / hostname",valueSourceName=nameof(PressAction),saveValue=false,applyOnLoad=false)]
    [ModOptionButton,ModOptionCategory("Direct IP",2),ModOptionOrder(2)]
    public static void EditAddressAction(int press=0) => Instance?.EditConnection("address");
    [ModOption("Paste IP from clipboard",valueSourceName=nameof(PressAction),saveValue=false,applyOnLoad=false)]
    [ModOptionButton,ModOptionCategory("Direct IP",2),ModOptionOrder(3)]
    public static void PasteAddressAction(int press=0) => Instance?.PasteConnectionEndpoint();
    [ModOption("Edit UDP port",valueSourceName=nameof(PressAction),saveValue=false,applyOnLoad=false)]
    [ModOptionButton,ModOptionCategory("Direct IP",2),ModOptionOrder(4)]
    public static void EditPortAction(int press=0) => Instance?.EditConnection("port");
    [ModOption("Edit player name",valueSourceName=nameof(PressAction),saveValue=false,applyOnLoad=false)]
    [ModOptionButton,ModOptionCategory("Player & hosting",3),ModOptionOrder(2)]
    public static void EditNameAction(int press=0) => Instance?.EditConnection("name");
    [ModOption("Edit server password",valueSourceName=nameof(PressAction),saveValue=false,applyOnLoad=false)]
    [ModOptionButton,ModOptionCategory("Player & hosting",3),ModOptionOrder(3)]
    public static void EditPasswordAction(int press=0) => Instance?.EditConnection("password");
}
