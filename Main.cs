using ExternalDynamicBoneEditor.IPCSupport;
using Harmony;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnhollowerBaseLib;
using UnhollowerBaseLib.Runtime;
using UnhollowerRuntimeLib.XrefScans;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using VRC;
using VRC.Core;
using ConsoleColor = System.ConsoleColor;
using IntPtr = System.IntPtr;

namespace DBMod
{
    internal class NDB : MelonMod
    {
        //public const int VERSION = 1035;
        public const string VERSION_STR = "1035";

        private static class NDBConfig
        {
            public static float distanceToDisable;
            public static float colliderSizeLimit;
            public static int dynamicBoneUpdateRate;
            public static bool distanceDisable;
            public static bool enabledByDefault;
            public static bool disallowInsideColliders;
            public static bool onlyForMyBones;
            public static bool onlyForMeAndFriends;
            public static bool disallowDesktoppers;
            public static bool enableFallbackModUi;
            public static int toggleButtonX;
            public static int toggleButtonY;
            public static bool enableBoundsCheck;
            public static float visiblityUpdateRate;
            public static bool onlyHandColliders;
            public static bool keybindsEnabled;
            public static bool onlyOptimize;
            public static int updateMode;
            public static bool hasShownCompatibilityIssueMessage;
            public static HashSet<string> avatarsToWhichNotApply;
            public static bool enableEditor;
            public static bool breastsOnly;
            public static bool enableUserPanelButton;
            public static int userPanelButtonX;
            public static int userPanelButtonY;
        }


        struct OriginalBoneInformation
        {
            public float updateRate;
            public float distanceToDisable;
            public List<DynamicBoneCollider> colliders;
            public DynamicBone referenceToOriginal;
            public bool distantDisable;
        }

        private static NDB _Instance;

        private Dictionary<string, System.Tuple<GameObject, bool, DynamicBone[], DynamicBoneCollider[], bool>> avatarsInScene;
        private Dictionary<string, List<OriginalBoneInformation>> originalSettings;
        public Dictionary<string, System.Tuple<Renderer, DynamicBone[]>> avatarRenderers;
        private GameObject localPlayer;
        private Dictionary<string, DynamicBone> localPlayerDBbyRootName;
        //private Transform localPlayerReferenceTransform;
        private Transform toggleButton;
        //private Transform onlyFriendsButton; //OnlyFans haha
        private bool enabled = true;

        private float nextUpdateVisibility = 0;
        private const float visiblityUpdateRate = 1f;

        private (MethodBase, MethodBase) reloadDynamicBoneParamInternalFuncs;

        private static AvatarInstantiatedDelegate onAvatarInstantiatedDelegate;
        private static PlayerLeftDelegate onPlayerLeftDelegate;
        private static JoinedRoom onJoinedRoom;

        private static void Hook(IntPtr target, IntPtr detour)
        {
            Imports.Hook(target, detour);
        }

        private Transform AddMenuButton(string butName, string butText, int butX, int butY, System.Action butAction)
        {
            Transform quickMenu = QuickMenu.prop_QuickMenu_0.transform;
            //RecursiveHierarchyDump(quickMenu, 0);

            // clone of a standard button
            Transform butTransform = UnityEngine.Object.Instantiate(quickMenu.Find("CameraMenu/BackButton").gameObject).transform;
            if (butTransform == null) MelonLogger.Log(ConsoleColor.Red, "Couldn't add button for dynamic bones");
            butTransform.name = butName;

            // set button's parent to quick menu
            butTransform.SetParent(quickMenu.Find("ShortcutMenu"), false);

            // set button's text
            butTransform.GetComponentInChildren<Text>().text = butText;

            // set position of new button based on existing menu buttons
            float buttonWidth = quickMenu.Find("UserInteractMenu/ForceLogoutButton").localPosition.x - quickMenu.Find("UserInteractMenu/BanButton").localPosition.x;
            float buttonHeight = quickMenu.Find("UserInteractMenu/ForceLogoutButton").localPosition.x - quickMenu.Find("UserInteractMenu/BanButton").localPosition.x;
            butTransform.localPosition = new Vector3(butTransform.localPosition.x + buttonWidth * butX, butTransform.localPosition.y + buttonHeight * butY, butTransform.localPosition.z);

            // Make it so the button does what we want
            butTransform.GetComponent<Button>().onClick = new Button.ButtonClickedEvent();
            butTransform.GetComponent<Button>().onClick.AddListener(butAction);

            // enable it just in case
            butTransform.gameObject.SetActive(true);

            return butTransform;
        }

        private void AddButtons()
        {
            toggleButton = this.AddMenuButton("NDBToggle", $"Press to {((enabled) ? "disable" : "enable")} Dynamic Bones mod", NDBConfig.toggleButtonX, NDBConfig.toggleButtonY, new System.Action(() =>
            {
                try
                {
                    ToggleState();
                }
                catch (System.Exception ex) { MelonLogger.Log(ConsoleColor.Red, ex.ToString()); }
            }));
        }

        public override void VRChat_OnUiManagerInit()
        {
            if (NDBConfig.enableFallbackModUi) AddButtons();

            if (NDBConfig.enableUserPanelButton) AddSelectiveButton();
        }

        private static void AddSelectiveButton()
        {
            GameObject button = UnityEngine.Object.Instantiate(QuickMenu.prop_QuickMenu_0.transform.Find("UserInteractMenu/BlockButton").gameObject);
            button.transform.SetParent(QuickMenu.prop_QuickMenu_0.transform.Find("UserInteractMenu"), false);
            button.transform.localPosition = new Vector3(button.transform.localPosition.x + (400f * NDBConfig.userPanelButtonX), button.transform.localPosition.y + (400f * NDBConfig.userPanelButtonY), button.transform.localPosition.z);
            button.GetComponentInChildren<Button>().onClick = new Button.ButtonClickedEvent();
            button.GetComponentInChildren<Button>().onClick.AddListener(new Action(() =>
            {
                button.GetComponentInChildren<Text>().text = $"{(NDBConfig.avatarsToWhichNotApply.Contains(QuickMenu.prop_QuickMenu_0.prop_APIUser_0.avatarId) ? "Enable" : "Disable")} MDB for this avatar";
                if (NDBConfig.avatarsToWhichNotApply.Contains(QuickMenu.prop_QuickMenu_0.prop_APIUser_0.avatarId))
                {
                    NDBConfig.avatarsToWhichNotApply.Remove(QuickMenu.prop_QuickMenu_0.prop_APIUser_0.avatarId);
                }
                else
                {
                    NDBConfig.avatarsToWhichNotApply.Add(QuickMenu.prop_QuickMenu_0.prop_APIUser_0.avatarId);
                }
                MelonPrefs.SetString("NDB", "AvatarsToWhichNotApply", NDBConfig.avatarsToWhichNotApply.Aggregate("", (acc, s) => acc += (';' + s)));
            }));
            button.GetComponentsInChildren<Text>()[1].text = "MDB disabled for this avatar";
            button.SetActive(true);
        }

        private delegate void AvatarInstantiatedDelegate(IntPtr @this, IntPtr avatarPtr, IntPtr avatarDescriptorPtr, bool loaded);
        private delegate void PlayerLeftDelegate(IntPtr @this, IntPtr playerPtr);
        private delegate void JoinedRoom(IntPtr @this);


        private void UiExpansionKit_AddSimpleMenuButton(Type uiKitApiType, int mode, string text, Action onClick, Action<GameObject> onShow)
        {
            uiKitApiType.GetMethod("RegisterSimpleMenuButton", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { mode, text, onClick, onShow });
        }

        [DllImport("User32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBox(IntPtr nWnd, string text, string title, uint type);

        public unsafe override void OnApplicationStart()
        {
            _Instance = this;

            RegisterModPrefs();

            OnModSettingsApplied();

            //if (NDBConfig.updateMode == 2)
            //{
            //    new Thread(new ThreadStart(CheckForUpdates)).Start();
            //}
            //else if (NDBConfig.updateMode == 0)
            //{
            //    int result = MessageBox(IntPtr.Zero, "Multiplayer Dynamic Bones can check for updates and notify you when one is avaiable. Do you want to enable update checks?", "Multiplayer Dynamic Bones mod", 0x04 | 0x40 | 0x1000 | 0x010000);
            //    if (result == 6)
            //    {
            //        NDBConfig.updateMode = 2;
            //    }
            //    else if (result == 7)
            //    {
            //        NDBConfig.updateMode = 1;
            //    }
            //    MelonPrefs.SetInt("NDB", "UpdateMode", NDBConfig.updateMode);
            //}

            enabled = NDBConfig.enabledByDefault;

            AddUI();

            HookCallbackFunctions();

            //to forcefully disable the limit
            PlayerPrefs.SetInt("VRC_LIMIT_DYNAMIC_BONE_USAGE", 0);

            MethodBase[] methods = XrefScanner.XrefScan(typeof(DynamicBone).GetMethod("OnValidate"))
                .Where(r => r.Type == XrefType.Method)
                .Select(xref => xref.TryResolve())
                .Where(m => m != null)
                .OrderBy(m => m.GetMethodBody().GetILAsByteArray().Length).ToArray();
            reloadDynamicBoneParamInternalFuncs = (methods[0], methods[1]);

            //Obsolete?
            //if (!NDBConfig.hasShownCompatibilityIssueMessage && MelonHandler.Mods.Any(m => m.Info.Name.ToLowerInvariant().Contains("emmvrc")))
            //{
            //    MessageBox(IntPtr.Zero, "Looks like you are using the 'emmVRC' mod. Please disable all emmVRC dynamic bones functionality in emmVRC settings to avoid compatibility issues with Multiplayer Dynamic Bones.", "Multiplayer Dynamic Bones mod", 0x40 | 0x1000 | 0x010000);
            //    MelonPrefs.SetBool("NDB", "HasShownCompatibilityIssueMessage", true);
            //}
        }

        //private void CheckForUpdates()
        //{
        //    string url = $"https://github.com/charlesdeepk/MultiplayerDynamicBonesMod/releases/tag/{VERSION + 1}";
        //    WebClient client = new WebClient();
        //    try
        //    {
        //        _ = client.DownloadString(url);
        //    }
        //    catch
        //    {
        //        return;
        //    }

        //    if (MessageBox(IntPtr.Zero, "There is an update avaiable for Multiplayer Dynamic Bones. Not updating could result in the mod not working or the game crashing. Do you want to launch the internet browser?", "Multiplayer Dynamic Bones mod", 0x04 | 0x40 | 0x1000) == 6)
        //    {
        //        Process.Start(url);
        //        MessageBox(IntPtr.Zero, "Please replace the file and restart VRChat for the update to apply", "Multiplayer Dynamic Bones mod", 0x40 | 0x1000);
        //    }

        //}

        private static unsafe void RegisterModPrefs()
        {
            MelonPrefs.RegisterCategory("NDB", "Multiplayer Dynamic Bones");
            MelonPrefs.RegisterBool("NDB", "EnabledByDefault", true, "Enabled by default");
            MelonPrefs.RegisterBool("NDB", "OnlyMe", false, "Only I can interact with other bones");
            MelonPrefs.RegisterBool("NDB", "OnlyFriends", false, "Only me and friends can interact with my and friend's bones");
            MelonPrefs.RegisterBool("NDB", "DisallowDesktoppers", false, "Desktoppers's colliders and bones won't be multiplayer'd");
            MelonPrefs.RegisterBool("NDB", "DistanceDisable", true, "Disable bones if beyond a distance");
            MelonPrefs.RegisterFloat("NDB", "DistanceToDisable", 4f, "Distance limit");
            MelonPrefs.RegisterBool("NDB", "DisallowInsideColliders", true, "Disallow inside colliders");
            MelonPrefs.RegisterFloat("NDB", "ColliderSizeLimit", 1f, "Collider size limit");
            MelonPrefs.RegisterInt("NDB", "DynamicBoneUpdateRate", 60, "Dynamic bone update rate");
            MelonPrefs.RegisterBool("NDB", "EnableModUI", true, "Enables mod UI", true);
            MelonPrefs.RegisterInt("NDB", "ButtonPositionX", 1, "X position of button", true);
            MelonPrefs.RegisterInt("NDB", "ButtonPositionY", 1, "Y position of button", true);
            MelonPrefs.RegisterBool("NDB", "EnableJustIfVisible", true, "Enable dynamic bones just if they are on view");
            MelonPrefs.RegisterFloat("NDB", "VisibilityUpdateRate", 1f, "Visibility update rate (seconds)");
            MelonPrefs.RegisterBool("NDB", "OnlyHandColliders", false, "Only enable colliders in hands");
            MelonPrefs.RegisterBool("NDB", "KeybindsEnabled", true, "Enable keyboard actuation(F1, F4 and F8)");
            MelonPrefs.RegisterBool("NDB", "OptimizeOnly", false, "Just optimize the dynamic bones of the scene, don't enable interaction");
            MelonPrefs.RegisterInt("NDB", "UpdateMode", 0, "A value of 2 will notify the user when a new version of the mod is avaiable, while 1 will not.");
            MelonPrefs.RegisterBool("NDB", "HasShownCompatibilityIssueMessage", false, null, true);
            MelonPrefs.RegisterString("NDB", "AvatarsToWhichNotApply", "", null, true);
            MelonPrefs.RegisterBool("NDB", "EnableEditor", false);
            MelonPrefs.RegisterBool("NDB", "OnlyDynamicBonesOnBreasts", false, "Only the breast bones will be made multiplayer");
            MelonPrefs.RegisterBool("NDB", "EnableUserPanelButton", false, "Enable the button that allows per-user dynamic bones enable or disable");
            MelonPrefs.RegisterInt("NDB", "UserPanelButtonX", 0, "X offset for the user panel button");
            MelonPrefs.RegisterInt("NDB", "UserPanelButtonY", -1, "Y offset for the user panel button");
        }

        private unsafe void HookCallbackFunctions()
        {
            try
            {
                IntPtr funcToHook = (IntPtr)typeof(VRCAvatarManager.MulticastDelegateNPublicSealedVoGaVRBoUnique).GetField("NativeMethodInfoPtr_Invoke_Public_Virtual_New_Void_GameObject_VRC_AvatarDescriptor_Boolean_0", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

                Hook(funcToHook, new System.Action<IntPtr, IntPtr, IntPtr, bool>(OnAvatarInstantiated).Method.MethodHandle.GetFunctionPointer());
                onAvatarInstantiatedDelegate = Marshal.GetDelegateForFunctionPointer<AvatarInstantiatedDelegate>(*(IntPtr*)funcToHook);
                MelonLogger.Log(ConsoleColor.Blue, $"Hooked OnAvatarInstantiated? {((onAvatarInstantiatedDelegate != null) ? "Yes!" : "No: critical error!!")}");

                funcToHook = (IntPtr)typeof(NetworkManager).GetField("NativeMethodInfoPtr_Method_Public_Void_Player_0", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                Hook(funcToHook, new System.Action<IntPtr, IntPtr>(OnPlayerLeft).Method.MethodHandle.GetFunctionPointer());
                onPlayerLeftDelegate = Marshal.GetDelegateForFunctionPointer<PlayerLeftDelegate>(*(IntPtr*)funcToHook);
                MelonLogger.Log(ConsoleColor.Blue, $"Hooked OnPlayerLeft? {((onPlayerLeftDelegate != null) ? "Yes!" : "No: critical error!!")}");

                funcToHook = (IntPtr)typeof(NetworkManager).GetFields(BindingFlags.NonPublic | BindingFlags.Static).Single((fi) => fi.Name.Contains("OnJoinedRoom")).GetValue(null);
                Hook(funcToHook, new System.Action<IntPtr>(OnJoinedRoom).Method.MethodHandle.GetFunctionPointer());
                onJoinedRoom = Marshal.GetDelegateForFunctionPointer<JoinedRoom>(*(IntPtr*)funcToHook);
                MelonLogger.Log(ConsoleColor.Blue, $"Hooked OnJoinedRoom? {((onJoinedRoom != null) ? "Yes!" : "No: critical error!!")}");
            }
            finally
            {
                if (onPlayerLeftDelegate == null || onAvatarInstantiatedDelegate == null || onJoinedRoom == null)
                {
                    this.enabled = false;
                    MelonLogger.Log(ConsoleColor.Red, "Multiplayer Dynamic Bones mod suffered a critical error! Mod version may be obsolete.");
                }
            }
            MelonLogger.Log(ConsoleColor.Green, $"NDBMod is {((enabled == true) ? "enabled" : "disabled")}");
        }

        private unsafe void AddUI()
        {
            Type uiKitApi = null;
            AppDomain.CurrentDomain.GetAssemblies().DoIf(_ => uiKitApi == null, ass => ass.GetTypes().DoIf(t => t.Name == "ExpansionKitApi", t => uiKitApi = t));
            MelonLogger.Log(ConsoleColor.DarkBlue, "Checking if UIExpansionKit (by knah) is present and adding mod UI");
            if (uiKitApi != null)
            {
                MelonLogger.Log(ConsoleColor.Blue, "UIExpansionKit is present");
                UiExpansionKit_AddSimpleMenuButton(uiKitApi, 0, $"Press to {((enabled == true) ? "disable" : "enable")} Multiplayer Dynamic Bones mod", () => ToggleState(), (button) => toggleButton = button.transform);
                NDBConfig.enableFallbackModUi = false;
            }
            else
            {
                MelonLogger.Log(ConsoleColor.Red, "UiExpansionKit is not present. Using fallback simple toggle button.");
            }


        }

        public override void OnModSettingsApplied()
        {
            NDBConfig.enabledByDefault = MelonPrefs.GetBool("NDB", "EnabledByDefault");
            NDBConfig.disallowInsideColliders = MelonPrefs.GetBool("NDB", "DisallowInsideColliders");
            NDBConfig.distanceToDisable = MelonPrefs.GetFloat("NDB", "DistanceToDisable");
            NDBConfig.distanceDisable = MelonPrefs.GetBool("NDB", "DistanceDisable");
            NDBConfig.colliderSizeLimit = MelonPrefs.GetFloat("NDB", "ColliderSizeLimit");
            NDBConfig.onlyForMyBones = MelonPrefs.GetBool("NDB", "OnlyMe");
            NDBConfig.onlyForMeAndFriends = MelonPrefs.GetBool("NDB", "OnlyFriends");
            NDBConfig.dynamicBoneUpdateRate = MelonPrefs.GetInt("NDB", "DynamicBoneUpdateRate");
            NDBConfig.disallowDesktoppers = MelonPrefs.GetBool("NDB", "DisallowDesktoppers");
            NDBConfig.enableFallbackModUi = MelonPrefs.GetBool("NDB", "EnableModUI");
            NDBConfig.toggleButtonX = MelonPrefs.GetInt("NDB", "ButtonPositionX");
            NDBConfig.toggleButtonY = MelonPrefs.GetInt("NDB", "ButtonPositionY");
            NDBConfig.enableBoundsCheck = MelonPrefs.GetBool("NDB", "EnableJustIfVisible");
            NDBConfig.visiblityUpdateRate = MelonPrefs.GetFloat("NDB", "VisibilityUpdateRate");
            NDBConfig.onlyHandColliders = MelonPrefs.GetBool("NDB", "OnlyHandColliders");
            NDBConfig.keybindsEnabled = MelonPrefs.GetBool("NDB", "KeybindsEnabled");
            NDBConfig.onlyOptimize = MelonPrefs.GetBool("NDB", "OptimizeOnly");
            NDBConfig.updateMode = MelonPrefs.GetInt("NDB", "UpdateMode");
            NDBConfig.hasShownCompatibilityIssueMessage = MelonPrefs.GetBool("NDB", "HasShownCompatibilityIssueMessage");
            NDBConfig.avatarsToWhichNotApply = new HashSet<string>(MelonPrefs.GetString("NDB", "AvatarsToWhichNotApply").Split(new char[] { ';' }));
            NDBConfig.enableEditor = MelonPrefs.GetBool("NDB", "EnableEditor");
            NDBConfig.breastsOnly = MelonPrefs.GetBool("NDB", "OnlyDynamicBonesOnBreasts");
            NDBConfig.enableUserPanelButton = MelonPrefs.GetBool("NDB", "OnlyDynamicBonesOnBreasts");
            NDBConfig.userPanelButtonX = MelonPrefs.GetInt("NDB", "UserPanelButtonX");
            NDBConfig.userPanelButtonY = MelonPrefs.GetInt("NDB", "UserPanelButtonY");
        }

        private static void OnJoinedRoom(IntPtr @this)
        {
            try
            {
                _Instance.originalSettings = new Dictionary<string, List<OriginalBoneInformation>>();
                _Instance.avatarsInScene = new Dictionary<string, System.Tuple<GameObject, bool, DynamicBone[], DynamicBoneCollider[], bool>>();
                _Instance.avatarRenderers = new Dictionary<string, System.Tuple<Renderer, DynamicBone[]>>();
                _Instance.localPlayer = null;

                onJoinedRoom(@this);
                //Console.WriteLine("ONJOINEDROOM PAST-CALLBACK");
                MelonLogger.Log(ConsoleColor.Blue, "New scene loaded; reset");
                //Console.WriteLine("ONJOINEDROOM SUCCESS");
            }
            catch(Exception e)
            {
                MelonLogger.LogError(e.ToString());
            }
        }

        private static void OnPlayerLeft(IntPtr @this, IntPtr playerPtr)
        {
            Player player = new Player(playerPtr);

            if (player.transform.root.gameObject.name.Contains("[Local]"))
            {
#if DEBUG
                MelonLogger.Log(ConsoleColor.Red, $"Not removing local player info");
#endif
                onPlayerLeftDelegate(@this, playerPtr);
                return;
            }

            if (!_Instance.avatarsInScene.ContainsKey(player.field_Internal_VRCPlayer_0.prop_String_0) && !_Instance.originalSettings.ContainsKey(player.field_Internal_VRCPlayer_0.prop_String_0))
            {

                onPlayerLeftDelegate(@this, playerPtr);
                //Console.WriteLine("ONPLAYERLEFT PAST-CALLBACK");
                return;

            }

            _Instance.RemoveBonesOfGameObjectInAllPlayers(_Instance.avatarsInScene[player.field_Internal_VRCPlayer_0.prop_String_0].Item4);
            _Instance.DeleteOriginalColliders(player.field_Internal_VRCPlayer_0.prop_String_0);
            _Instance.RemovePlayerFromDict(player.field_Internal_VRCPlayer_0.prop_String_0);
            _Instance.RemoveDynamicBonesFromVisibilityList(player.field_Internal_VRCPlayer_0.prop_String_0);
            MelonLogger.Log(ConsoleColor.Blue, $"Player {player.field_Internal_VRCPlayer_0.prop_String_0} left the room so all his dynamic bones info was deleted");
            onPlayerLeftDelegate(@this, playerPtr);
            //Console.WriteLine("ONPLAYERLEFT SUCCESS");
        }

        private static void RecursiveHierarchyDump(Transform child, int c)
        {
            StringBuilder offs = new StringBuilder();
            for (int i = 0; i < c; i++) offs.Append('-');
            offs.Append(child.name);
            offs.Append("  Components: ");
            child.GetComponents<Component>().Do((b) => offs.Append(b.ToString() + " | "));
            MelonLogger.Log(ConsoleColor.White, offs.ToString());
            for (int x = 0; x < child.childCount; x++)
            {
                RecursiveHierarchyDump(child.GetChild(x), c + 1);

            }
        }

        //private static bool hasDumpedIt = false;
        private static void OnAvatarInstantiated(IntPtr @this, IntPtr avatarPtr, IntPtr avatarDescriptorPtr, bool loaded)
        {
            //Console.WriteLine("ONAVATARINSTANTIATED START");
            onAvatarInstantiatedDelegate(@this, avatarPtr, avatarDescriptorPtr, loaded);
            //Console.WriteLine("ONAVATARINSTANTIATED PAST-CALLBACK");

            try
            {
                if (loaded)
                {
                    GameObject avatar = new GameObject(avatarPtr);
                    //VRC.SDKBase.VRC_AvatarDescriptor avatarDescriptor = new VRC.SDKBase.VRC_AvatarDescriptor(avatarDescriptorPtr);


                    if (avatar.transform.root.gameObject.name.Contains("[Local]"))
                    {
                        avatar.GetComponentsInChildren<DynamicBone>(true).Do(b => { if (b.m_Root == null) UnityEngine.Object.Destroy(b); });
                        _Instance.localPlayer = avatar;
                        try
                        {
                            _Instance.localPlayerDBbyRootName = avatar.GetComponentsInChildren<DynamicBone>().ToDictionary((b) => b.GetInstanceID().ToString());
                        }
                        catch (Exception ex) { MelonLogger.LogError(ex.ToString()); }
                    }

                    _Instance.AddOrReplaceWithCleanup(
                        avatar.transform.root.GetComponentInChildren<VRCPlayer>().prop_String_0,
                        new System.Tuple<GameObject, bool, DynamicBone[], DynamicBoneCollider[], bool>(
                            avatar,
                            avatar.transform.root.GetComponentInChildren<VRCPlayer>().prop_VRCPlayerApi_0.IsUserInVR(),
                            avatar.GetComponentsInChildren<DynamicBone>(),
                            avatar.GetComponentsInChildren<DynamicBoneCollider>(),
                            APIUser.IsFriendsWith(avatar.transform.root.GetComponentInChildren<Player>().prop_APIUser_0.id)));

                    MelonLogger.Log(ConsoleColor.Blue, "New avatar loaded, added to avatar list");
                    MelonLogger.Log(ConsoleColor.Green, $"Added {avatar.transform.root.GetComponentInChildren<VRCPlayer>().prop_String_0}");
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.LogError("An exception was thrown while working!\n" + ex.ToString());
            }

            //Console.WriteLine("ONAVATARINSTANTIATED SUCCESS");
        }

        public void AddOrReplaceWithCleanup(string key, System.Tuple<GameObject, bool, DynamicBone[], DynamicBoneCollider[], bool> newValue)
        {
            foreach (DynamicBoneCollider col in newValue.Item4)
            {
                if (NDBConfig.disallowInsideColliders && col.m_Bound == DynamicBoneCollider.EnumNPublicSealedvaOuIn3vUnique.Inside)
                {
                    newValue.Item3.Do((b) => b.m_Colliders.Remove(col));
                    MelonLogger.Log(ConsoleColor.Yellow, $"Removing bone {col.transform.name} because settings disallow inside colliders");
                    UnityEngine.Object.Destroy(col);
                }
            }

            if (!avatarsInScene.ContainsKey(key ?? ""))
            {
                SaveOriginalColliderList(key, newValue.Item3);
                AddToPlayerDict(key, newValue);
            }
            else
            {
                DeleteOriginalColliders(key);
                SaveOriginalColliderList(key, newValue.Item3);
                DynamicBoneCollider[] oldColliders = avatarsInScene[key].Item4;
                RemovePlayerFromDict(key);
                AddToPlayerDict(key, newValue);
                RemoveBonesOfGameObjectInAllPlayers(oldColliders);
                RemoveDynamicBonesFromVisibilityList(key);
                MelonLogger.Log(ConsoleColor.Blue, $"User {key} swapped avatar, system updated");
            }
            if (enabled) AddCollidersToAllPlayers(newValue);
            //if (enabled) AddBonesOfGameObjectToAllPlayers(newValue);
            if (newValue.Item1 != localPlayer) AddDynamicBonesToVisibilityList(key, newValue.Item3, newValue.Item1.GetComponentInChildren<SkinnedMeshRenderer>());
        }

        private bool SelectBonesWithRules(KeyValuePair<string, System.Tuple<GameObject, bool, DynamicBone[], DynamicBoneCollider[], bool>> item)
        {
            bool valid = true;
            if (NDBConfig.onlyForMyBones) valid &= item.Value.Item1 == localPlayer;
            if (NDBConfig.onlyForMeAndFriends) valid &= item.Value.Item5 || (item.Value.Item1 == localPlayer);
            if (NDBConfig.disallowDesktoppers) valid &= item.Value.Item2 || (item.Value.Item1 == localPlayer);
            return valid;
        }

        private bool SelectCollidersWithRules(KeyValuePair<string, System.Tuple<GameObject, bool, DynamicBone[], DynamicBoneCollider[], bool>> item)
        {
            bool valid = true;
            if (NDBConfig.onlyForMeAndFriends) valid &= item.Value.Item5 || (item.Value.Item1 == localPlayer);
            if (NDBConfig.disallowDesktoppers) valid &= item.Value.Item2;
            return valid;
        }

        private bool ColliderMeetsRules(DynamicBoneCollider coll, System.Tuple<GameObject, bool, DynamicBone[], DynamicBoneCollider[], bool> item)
        {
            bool valid = true;
            if (NDBConfig.onlyHandColliders) valid &= coll.transform.IsChildOf(item.Item1.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.LeftHand).parent) || coll.transform.IsChildOf(item.Item1.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.RightHand).parent);
            return valid;
        }

        private void ApplyBoneSettings(DynamicBone bone)
        {
            bone.m_DistantDisable = NDBConfig.distanceDisable;
            bone.m_DistanceToObject = NDBConfig.distanceToDisable;
            bone.m_UpdateRate = NDBConfig.dynamicBoneUpdateRate;
            bone.m_ReferenceObject = localPlayer?.transform ?? bone.m_ReferenceObject;
        }

        private void AddAllCollidersToAllPlayers()
        {
            foreach (System.Tuple<GameObject, bool, DynamicBone[], DynamicBoneCollider[], bool> player in avatarsInScene.Values)
            {
                AddCollidersToAllPlayers(player);
                //AddBonesOfGameObjectToAllPlayers(player);
            }
        }

        private void AddCollidersToAllPlayers(System.Tuple<GameObject, bool, DynamicBone[], DynamicBoneCollider[], bool> player)
        {
            foreach (var collider in player.Item3)
            {
                ApplyBoneSettings(collider);
            }

            if (NDBConfig.onlyOptimize) return;

            if ((NDBConfig.disallowDesktoppers && !player.Item2) || (NDBConfig.onlyForMeAndFriends && !player.Item5 && player.Item1 != localPlayer) || (NDBConfig.onlyForMyBones && player.Item1 != localPlayer)) return;
            foreach (var otherPlayerInfo in avatarsInScene.Values)
            {
                try
                {
                    if ((otherPlayerInfo.Item1 == player.Item1) || (NDBConfig.disallowDesktoppers && !player.Item2 && player.Item1 != localPlayer) || (NDBConfig.onlyForMeAndFriends && !player.Item5 && player.Item1 != localPlayer)) continue;
                    foreach (DynamicBone otherPlayerDynamicBone in otherPlayerInfo.Item3)
                    {
                        if (NDBConfig.breastsOnly && (otherPlayerDynamicBone.transform.root.GetComponentInChildren<VRCPlayer>().field_Internal_Animator_0.isHuman && (!otherPlayerDynamicBone.transform.IsChildOf(otherPlayerDynamicBone.transform.root.GetComponentInChildren<VRCPlayer>().field_Internal_Animator_0.GetBoneTransform(HumanBodyBones.Chest))))) continue;
                        foreach (DynamicBoneCollider collider in player.Item4)
                        {
                            if (NDBConfig.onlyHandColliders && otherPlayerInfo.Item1.transform.root.GetComponentInChildren<VRCPlayer>().field_Internal_Animator_0.isHuman && (collider.transform.IsChildOf(otherPlayerInfo.Item1.transform.root.GetComponentInChildren<VRCPlayer>().field_Internal_Animator_0.GetBoneTransform(HumanBodyBones.LeftHand)) || collider.transform.IsChildOf(otherPlayerInfo.Item1.transform.root.GetComponentInChildren<VRCPlayer>().field_Internal_Animator_0.GetBoneTransform(HumanBodyBones.RightHand)))) continue;
                            try
                            {
                                AddColliderToBone(otherPlayerDynamicBone, collider);

                            }
                            catch (Exception ex) { MelonLogger.Log(ConsoleColor.Red, ex.ToString()); }
                        }
                    }
                }
                catch { };
            }

            foreach (var otherPlayerInfo in avatarsInScene.Values)
            {
                try
                {
                    if ((otherPlayerInfo.Item1 == player.Item1) || (NDBConfig.disallowDesktoppers && !player.Item2 && player.Item1 != localPlayer) || (NDBConfig.onlyForMeAndFriends && !player.Item5 && player.Item1 != localPlayer)) continue;
                    foreach (DynamicBoneCollider otherCollider in otherPlayerInfo.Item4)
                    {
                        if (NDBConfig.onlyHandColliders && otherPlayerInfo.Item1.transform.root.GetComponentInChildren<VRCPlayer>().field_Internal_Animator_0.isHuman && (otherCollider.transform.IsChildOf(otherPlayerInfo.Item1.transform.root.GetComponentInChildren<VRCPlayer>().field_Internal_Animator_0.GetBoneTransform(HumanBodyBones.LeftHand)) || otherCollider.transform.IsChildOf(otherPlayerInfo.Item1.transform.root.GetComponentInChildren<VRCPlayer>().field_Internal_Animator_0.GetBoneTransform(HumanBodyBones.RightHand)))) continue;
                        foreach (DynamicBone dynamicBone in player.Item3)
                        {
                            if (NDBConfig.breastsOnly && (dynamicBone.transform.root.GetComponentInChildren<VRCPlayer>().field_Internal_Animator_0.isHuman && (!dynamicBone.transform.IsChildOf(dynamicBone.transform.root.GetComponentInChildren<VRCPlayer>().field_Internal_Animator_0.GetBoneTransform(HumanBodyBones.Chest))))) continue;
                            AddColliderToBone(dynamicBone, otherCollider);
                        }
                    }
                }
                catch { };
            }

        }

        private void AddBonesOfGameObjectToAllPlayers(System.Tuple<GameObject, bool, DynamicBone[], DynamicBoneCollider[], bool> player)
        {
            if (player.Item1 == localPlayer) return;
            if (NDBConfig.onlyForMeAndFriends)
            {
                if (!player.Item5)
                {
                    MelonLogger.Log(ConsoleColor.DarkYellow, $"Not adding bones of player {avatarsInScene.First((x) => x.Value.Item1 == player.Item1).Key} because settings only allow friends");
                    return;
                }
            }
            if (NDBConfig.disallowDesktoppers)
            {
                if (!player.Item2)
                {
                    MelonLogger.Log(ConsoleColor.DarkYellow, $"Not adding bones of player {avatarsInScene.First((x) => x.Value.Item1 == player.Item1).Key} because settings disallow desktopper");
                    return;
                }
            }


            foreach (DynamicBone db in player.Item3)
            {
                if (db == null) continue;
                ApplyBoneSettings(db);
            }

            if (NDBConfig.onlyOptimize) return;

            foreach (DynamicBone[] playersColliders in avatarsInScene.Where((x) => SelectBonesWithRules(x) && x.Value.Item1 != player.Item1).Select((x) => x.Value.Item3))
            {
                foreach (DynamicBone playerBone in playersColliders)
                {
                    foreach (DynamicBoneCollider playerCollider in player.Item4)
                    {
                        if (ColliderMeetsRules(playerCollider, player))
                        {
                            AddColliderToBone(playerBone, playerCollider);
                        }
                    }
                }
            }
        }

        private void RemoveBonesOfGameObjectInAllPlayers(DynamicBoneCollider[] colliders)
        {
            foreach (DynamicBone[] dbs in avatarsInScene.Values.Select((x) => x.Item3))
            {
                foreach (DynamicBone db in dbs)
                {
                    foreach (DynamicBoneCollider dbc in colliders)
                    {
                        db.m_Colliders.Remove(dbc);
                    }
                }
            }
        }

        private void AddColliderToDynamicBone(DynamicBone bone, DynamicBoneCollider dbc)
        {
            if (bone == null || dbc == null) return;
#if DEBUG
            MelonModLogger.Log(ConsoleColor.Cyan, $"Adding {bone.m_Root.name} to {dbc.gameObject.name}");
#endif
            //MelonModLogger.Log(ConsoleColor.Cyan, $"Adding {dbc.gameObject.name} to {bone.m_Root.name}");
            if (!bone.m_Colliders.Contains(dbc)) bone.m_Colliders.Add(dbc);

        }

        private void AddColliderToBone(DynamicBone bone, DynamicBoneCollider collider)
        {
            if (NDBConfig.disallowInsideColliders && collider.m_Bound == DynamicBoneCollider.EnumNPublicSealedvaOuIn3vUnique.Inside)
            {
                return;
            }

            if (collider.m_Radius > NDBConfig.colliderSizeLimit || collider.m_Height > NDBConfig.colliderSizeLimit)
            {
                return;
            }

            AddColliderToDynamicBone(bone, collider);
        }

        private void AddDynamicBonesToVisibilityList(string player, DynamicBone[] dynamicBones, Renderer renderer)
        {
            avatarRenderers.Add(player, new System.Tuple<Renderer, DynamicBone[]>(renderer, dynamicBones));
        }

        private void RemoveDynamicBonesFromVisibilityList(string player)
        {
            avatarRenderers.Remove(player);
        }



        public override void OnUpdate()
        {
            if (avatarRenderers != null)
            {
                if (avatarRenderers.Count != 0 && NDBConfig.enableBoundsCheck) EnableIfVisible();
            }

            if (!NDBConfig.keybindsEnabled) return;

            if (Input.GetKeyDown(KeyCode.F8))
            {
                MelonLogger.Log(ConsoleColor.DarkMagenta, "My bones have the following colliders attached:");
                localPlayer.GetComponentsInChildren<DynamicBone>().Do((bone) =>
                {
                    MelonLogger.Log(ConsoleColor.DarkMagenta, $"Bone {bone.m_Root.name} has {bone.m_Colliders.Count} colliders attached");
                    bone.m_Colliders._items.Do((dbc) =>
                    {
                        try
                        {
                            MelonLogger.Log(ConsoleColor.DarkMagenta, $"Bone {bone?.m_Root.name ?? "null"} has {dbc?.gameObject.name ?? "null"}");
                        }
                        catch (System.Exception ex) { MelonLogger.Log(ConsoleColor.Red, ex.ToString()); };
                    });
                });

                MelonLogger.Log(ConsoleColor.DarkMagenta, $"There are {avatarsInScene.Values.Aggregate(0, (acc, tup) => acc += tup.Item3.Length)} Dynamic Bones in scene");
                MelonLogger.Log(ConsoleColor.DarkMagenta, $"There are {avatarsInScene.Values.Aggregate(0, (acc, tup) => acc += tup.Item4.Length)} Dynamic Bones Colliders in scene");
            }

            if (Input.GetKeyDown(KeyCode.F9))
            {
                MelonLogger.Log(ConsoleColor.DarkMagenta, "Another player bones have the following colliders attached:");
                avatarsInScene.First(i => i.Value.Item1 != localPlayer).Value.Item1.GetComponentsInChildren<DynamicBone>().Do((bone) =>
                {
                    MelonLogger.Log(ConsoleColor.DarkMagenta, $"Bone {bone.m_Root.name} has {bone.m_Colliders.Count} colliders attached");
                    bone.m_Colliders._items.Do((dbc) =>
                    {
                        try
                        {
                            MelonLogger.Log(ConsoleColor.DarkMagenta, $"Bone {bone?.m_Root.name ?? "null"} has {dbc?.gameObject.name ?? "null"}");
                        }
                        catch (System.Exception ex) { MelonLogger.Log(ConsoleColor.Red, ex.ToString()); };
                    });
                });
            }

            if (Input.GetKeyDown(KeyCode.F1))
            {
                ToggleState();
            }

            if (Input.GetKeyDown(KeyCode.F4))
            {
                MelonLogger.Log(ConsoleColor.Red, "List of avatar in dict:");
                foreach (string name in avatarsInScene.Keys)
                {
                    MelonLogger.Log(ConsoleColor.DarkGreen, name);
                }
            }

            if (Input.GetKeyDown(KeyCode.F5))
            {
                if (NDBConfig.enableEditor) ToggleDynamicBoneEditorGUI();
            }

        }

        private Rect guiRect;
        private bool showEditorGUI = false;

        private Process editorProccess;
        private string editorPath;
        private IPCHandler ipcHandler;
        private void ToggleDynamicBoneEditorGUI()
        {
            showEditorGUI = !showEditorGUI;
            new Thread(new ThreadStart(ConnectToExternalInterface));
            //if (editorProccess != null && !editorProccess.HasExited)
            //{
            //    editorProccess.CloseMainWindow();
            //    editorProccess = null;
            //}
            //else
            //{
            //    if (editorPath == null)
            //    {
            //        if (!File.Exists(editorPath))
            //        {
            //            editorPath = Path.Combine(Assembly.Location, "ExternalDynamicBoneEditor.exe");
            //            //using (ResourceReader resourceReader = new ResourceReader(Assembly.GetManifestResourceStream(Assembly.GetManifestResourceNames()[0])))
            //            //{
            //            //    editorPath = Path.Combine(Assembly.Location, "ExternalDynamicBoneEditor.exe");
            //            //    resourceReader.GetResourceData("ExternalDynamicBoneEditor", out string _, out byte[] bytecode);
            //            //    File.WriteAllBytes(editorPath, bytecode);
            //            //}
            //        }
            //    }
            //    editorProccess = Process.Start(editorPath);
            //    new Thread(new ParameterizedThreadStart(ConnectToExternalInterface)).Start(new Tuple<string, DynamicBone[]>(localPlayer.transform.root.GetComponentInChildren<VRCPlayer>().prop_ApiAvatar_0.name.Take(127).Aggregate("", (acc, c) => acc += c), (DynamicBone[])localPlayer.GetComponentsInChildren<DynamicBone>()));
            //}
        }

        private void ConnectToExternalInterface()
        {
            try
            {
                AvatarBones avatar = new AvatarBones();
                avatar.name = localPlayer.transform.root.GetComponentInChildren<VRCPlayer>().prop_ApiAvatar_0.name.Take(127).Aggregate("", (acc, c) => acc += c);
                MelonLogger.Log(ConsoleColor.Blue, "1");
                foreach (DynamicBone db in localPlayer.GetComponentsInChildren<DynamicBone>())
                {
                    avatar.bones[avatar.boneCount++] = MakeBoneStruct(db);
                }
                MelonLogger.Log(ConsoleColor.Blue, "2");
                ipcHandler = new IPCHandler(new NamedPipeServerStream("vrchatmdb", PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message));
                while (!ipcHandler.IsConnected) Thread.Sleep(100);
                HandleIPCMessages(localPlayer.GetComponentsInChildren<DynamicBone>());
            }
            catch (Exception e) { MelonLogger.Log(e.ToString()); }
        }

        private void HandleIPCMessages(DynamicBone[] dynamicBones)
        {
            Message kind = ipcHandler.Receive(out byte[] msg);
            using (MemoryStream ms = new MemoryStream(msg))
            {
                using (BinaryReader br = new BinaryReader(ms))
                {
                    switch (kind)
                    {
                        case Message.SetBoneDamping:
                            {
                                GetDynamicBoneByName(dynamicBones, br.ReadString()).m_Damping = br.ReadSingle();
                                break;
                            }
                        case Message.SetBoneElasticity:
                            {
                                GetDynamicBoneByName(dynamicBones, br.ReadString()).m_Elasticity = br.ReadSingle();
                                break;
                            }
                        case Message.SetBoneStiffness:
                            {
                                GetDynamicBoneByName(dynamicBones, br.ReadString()).m_Stiffness = br.ReadSingle();
                                break;
                            }
                        case Message.SetBoneInert:
                            {
                                GetDynamicBoneByName(dynamicBones, br.ReadString()).m_Inert = br.ReadSingle();
                                break;
                            }
                        case Message.SetBoneRadius:
                            {
                                GetDynamicBoneByName(dynamicBones, br.ReadString()).m_Radius = br.ReadSingle();
                                break;
                            }
                        case Message.SetBoneEndLength:
                            {
                                GetDynamicBoneByName(dynamicBones, br.ReadString()).m_EndLength = br.ReadSingle();
                                break;
                            }
                        case Message.SetBoneEndOffset:
                            {
                                GetDynamicBoneByName(dynamicBones, br.ReadString()).m_EndOffset = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                                break;
                            }
                        case Message.SetBoneGravity:
                            {
                                GetDynamicBoneByName(dynamicBones, br.ReadString()).m_Gravity = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                                break;
                            }
                        case Message.SetBoneForce:
                            {
                                GetDynamicBoneByName(dynamicBones, br.ReadString()).m_Force = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                                break;
                            }
                    }
                }
            }

        }

        private DynamicBone GetDynamicBoneByName(DynamicBone[] bones, string name)
        {
            return bones.Single(b => b.m_Root.name == name);
        }

        private SerializedBoneData MakeBoneStruct(DynamicBone db)
        {
            SerializedBoneData serializedBoneData = new SerializedBoneData();
            serializedBoneData.name = db.m_Root?.name ?? "undefined";
            serializedBoneData.damping = db.m_Damping;
            serializedBoneData.elasticity = db.m_Elasticity;
            serializedBoneData.stiffness = db.m_Stiffness;
            serializedBoneData.inert = db.m_Inert;
            serializedBoneData.radius = db.m_Radius;
            serializedBoneData.endLength = db.m_EndLength;
            serializedBoneData.endOffset = new float3(db.m_EndOffset.x, db.m_EndOffset.y, db.m_EndOffset.z);
            serializedBoneData.gravity = new float3(db.m_Gravity.x, db.m_Gravity.y, db.m_Gravity.z);
            serializedBoneData.force = new float3(db.m_Force.x, db.m_Force.y, db.m_Force.z);
            return serializedBoneData;
        }

        public override void OnGUI()
        {
            if (showEditorGUI) guiRect = GUILayout.Window(0, new Rect(5, 5, 80, 80), (GUI.WindowFunction)DrawWindowContents, "Dynamic Bones Editor Interface", new Il2CppReferenceArray<GUILayoutOption>(0));
            GUI.FocusWindow(0);
        }

        private Vector2 scrollPosition;
        private void DrawWindowContents(int id)
        {
            try
            {
                //MelonModLogger.Log(ConsoleColor.DarkBlue, "Started drawing editor UI");
                GUILayout.Label($"Avatar: {localPlayer.GetComponentInChildren<VRCPlayer>()?.prop_VRCAvatarManager_0?.prop_ApiAvatar_0?.name ?? ("error fetching avatar name")}", new GUIStyle() { fontSize = 18 }, new Il2CppReferenceArray<GUILayoutOption>(0));
                scrollPosition = GUILayout.BeginScrollView(scrollPosition, new GUILayoutOption[] { GUILayout.Width(400), GUILayout.Height(600) });
                foreach (DynamicBone db in localPlayer.GetComponentsInChildren<DynamicBone>(true))
                {
                    //MelonModLogger.Log(ConsoleColor.DarkBlue, $"Started drawing bone {db.m_Root.name}");
                    GUILayout.Label(db.m_Root.name, new GUIStyle() { fontSize = 18 }, new Il2CppReferenceArray<GUILayoutOption>(0));
                    GUILayout.Label("Update rate", new GUIStyle() { fontSize = 14 }, new Il2CppReferenceArray<GUILayoutOption>(0));
                    db.m_UpdateRate = (int)GUILayout.HorizontalSlider(db.m_UpdateRate, 1f, 60f, new Il2CppReferenceArray<GUILayoutOption>(0));
                    //if (int.TryParse(GUILayout.TextField(((int)db.m_UpdateRate).ToString(), new GUIStyle() { margin = new RectOffset(10, 0, 0, 0) }, new Il2CppReferenceArray<GUILayoutOption>(0)), out int updateRatevalue))
                    //{
                    //    db.m_UpdateRate = updateRatevalue;
                    //}

                    GUILayout.Label("Damping", new GUIStyle() { fontSize = 14 }, new Il2CppReferenceArray<GUILayoutOption>(0));
                    db.m_Damping = GUILayout.HorizontalSlider(db.m_Damping, 0f, 1f, new Il2CppReferenceArray<GUILayoutOption>(0));
                    GUILayout.Label("Elasticity", new GUIStyle() { fontSize = 14 }, new Il2CppReferenceArray<GUILayoutOption>(0));
                    db.m_Elasticity = GUILayout.HorizontalSlider(db.m_Elasticity, 0f, 1f, new Il2CppReferenceArray<GUILayoutOption>(0));
                    GUILayout.Label("Stiffness", new GUIStyle() { fontSize = 14 }, new Il2CppReferenceArray<GUILayoutOption>(0));
                    db.m_Stiffness = GUILayout.HorizontalSlider(db.m_Stiffness, 0f, 1f, new Il2CppReferenceArray<GUILayoutOption>(0));
                    GUILayout.Label("Inert", new GUIStyle() { fontSize = 14 }, new Il2CppReferenceArray<GUILayoutOption>(0));
                    db.m_Inert = GUILayout.HorizontalSlider(db.m_Inert, 0f, 1f, new Il2CppReferenceArray<GUILayoutOption>(0));

                    GUILayout.Label("Radius", new GUIStyle() { fontSize = 14 }, new Il2CppReferenceArray<GUILayoutOption>(0));
                    if (float.TryParse(GUILayout.TextField((db.m_Radius).ToString(), new Il2CppReferenceArray<GUILayoutOption>(0)), out float radiusValue))
                    {
                        db.m_Radius = radiusValue;
                    }

                    GUILayout.Label("End length", new GUIStyle() { fontSize = 14 }, new Il2CppReferenceArray<GUILayoutOption>(0));
                    if (float.TryParse(GUILayout.TextField((db.m_EndLength).ToString(), new Il2CppReferenceArray<GUILayoutOption>(0)), out float endLengthValue))
                    {
                        db.m_Radius = endLengthValue;
                    }
                    GUILayout.Label("End offset", new GUIStyle() { fontSize = 14 }, new Il2CppReferenceArray<GUILayoutOption>(0));
                    GUILayout.Label("X", new GUIStyle() { fontSize = 14 }, Array.Empty<GUILayoutOption>());
                    //if (float.TryParse(GUILayout.TextField((db.m_EndOffset.x).ToString(), new GUIStyle() { margin = new RectOffset((int)GUILayoutUtility.GetLastRect().xMax, 0, 0, 0) }, new Il2CppReferenceArray<GUILayoutOption>(0)), out float xvalue))
                    if (float.TryParse(GUILayout.TextField((db.m_EndOffset.x).ToString(), new Il2CppReferenceArray<GUILayoutOption>(0)), out float xvalue))
                    {
                        db.m_EndOffset.Set(xvalue, db.m_EndOffset.y, db.m_EndOffset.z);
                    }
                    GUILayout.Label("Y", new GUIStyle() { fontSize = 14 }, new Il2CppReferenceArray<GUILayoutOption>(0));
                    if (float.TryParse(GUILayout.TextField((db.m_EndOffset.y).ToString(), new Il2CppReferenceArray<GUILayoutOption>(0)), out float yvalue))
                    {
                        db.m_EndOffset.Set(db.m_EndOffset.x, yvalue, db.m_EndOffset.z);
                    }
                    GUILayout.Label("Z", new GUIStyle() { fontSize = 14 }, new Il2CppReferenceArray<GUILayoutOption>(0));
                    if (float.TryParse(GUILayout.TextField((db.m_EndOffset.z).ToString(), new Il2CppReferenceArray<GUILayoutOption>(0)), out float zvalue))
                    {
                        db.m_EndOffset.Set(db.m_EndOffset.x, db.m_EndOffset.y, zvalue);
                    }
                }

                GUILayout.EndScrollView();
                if (GUI.changed)
                {
                    foreach (DynamicBone db in localPlayer.GetComponentsInChildren<DynamicBone>(true))
                    {
                        db.m_Radius = Mathf.Max(db.m_Radius, 0f);
                        reloadDynamicBoneParamInternalFuncs.Item1.Invoke(db, null);
                        reloadDynamicBoneParamInternalFuncs.Item2.Invoke(db, null);
                        MelonLogger.Log(ConsoleColor.DarkGreen, $"Updated setting for bone {db.m_Root.name}");
                    }
                }


                //MelonModLogger.Log(ConsoleColor.DarkBlue, "Finished drawing editor UI");
            }
            catch (Exception ex)
            {
                MelonLogger.LogError(ex.ToString());
            }
        }

        private void EnableIfVisible()
        {
            if (nextUpdateVisibility < Time.time)
            {
                foreach (System.Tuple<Renderer, DynamicBone[]> go in avatarRenderers.Values)
                {
                    if (go.Item1 == null) continue;
                    bool visible = go.Item1.isVisible;
                    foreach (DynamicBone db in go.Item2)
                    {
#if DEBUG
                        if (db.enabled != visible) MelonModLogger.Log(ConsoleColor.DarkBlue, $"{db.gameObject.name} is now {((visible) ? "enabled" : "disabled")}");
#endif
                        db.enabled = visible;
                    }
                }
                nextUpdateVisibility = Time.time + NDBConfig.visiblityUpdateRate;
            }
        }

        private void ToggleState()
        {
            enabled = !enabled;
            MelonLogger.Log(ConsoleColor.Green, $"NDBMod is now {((enabled == true) ? "enabled" : "disabled")}");
            try
            {
                if (!enabled)
                {
                    RestoreOriginalColliderList();
                }
                else AddAllCollidersToAllPlayers();
            }
            catch (Exception ex) { MelonLogger.Log(ConsoleColor.Red, ex.ToString()); }

            try
            {
                toggleButton.GetComponentInChildren<Text>().text = $"Press to {((enabled == true) ? "disable" : "enable")} Multiplayer Dynamic Bones mod";
            }
            catch { }
            if (NDBConfig.enableFallbackModUi) toggleButton.GetComponentInChildren<Text>().text = $"Press to {((enabled) ? "disable" : "enable")} Dynamic Bones mod";
        }

        private void RemovePlayerFromDict(string name)
        {
            avatarsInScene.Remove(name);
        }

        private void AddToPlayerDict(string name, System.Tuple<GameObject, bool, DynamicBone[], DynamicBoneCollider[], bool> value)
        {
            avatarsInScene.Add(name, value);
        }

        private void DeleteOriginalColliders(string name)
        {
            originalSettings.Remove(name);
        }

        private void SaveOriginalColliderList(string name, DynamicBone[] bones)
        {
            if (originalSettings.ContainsKey(name)) originalSettings.Remove(name);
            List<OriginalBoneInformation> ogInfo = new List<OriginalBoneInformation>(bones.Length);
            foreach (DynamicBone b in bones)
            {
                bones.Select((bone) =>
                {
                    return new OriginalBoneInformation() { distanceToDisable = bone.m_DistanceToObject, updateRate = bone.m_UpdateRate, distantDisable = bone.m_DistantDisable, colliders = new List<DynamicBoneCollider>(bone.m_Colliders.ToArrayExtension()), referenceToOriginal = bone };
                }).Do((info) => ogInfo.Add(info));
            }
            originalSettings.Add(name, ogInfo);
            MelonLogger.Log(ConsoleColor.DarkGreen, $"Saved original dynamic bone info of player {name}");
        }

        private void RestoreOriginalColliderList()
        {
            foreach (KeyValuePair<string, Tuple<GameObject, bool, DynamicBone[], DynamicBoneCollider[], bool>> player in avatarsInScene)
            {
                MelonLogger.Log(ConsoleColor.DarkBlue, $"Restoring original settings for player {player.Key}");
                foreach (DynamicBone db in player.Value.Item3)
                {
                    if (originalSettings.TryGetValue(player.Key, out List<OriginalBoneInformation> origList))
                    {
                        try
                        {
                            origList.DoIf((x) => ReferenceEquals(x.referenceToOriginal, db), (origData) =>
                            {
                                db.m_Colliders.Clear();
                                origData.colliders.ForEach((dbc) => db.m_Colliders.Add(dbc));
                                db.m_DistanceToObject = origData.distanceToDisable;
                                db.m_UpdateRate = origData.updateRate;
                                db.m_DistantDisable = origData.distantDisable;
                            });
                        }
                        catch (Exception e)
                        {
                            MelonLogger.Log(ConsoleColor.Red, e.ToString());
                        }
                    }
                    else
                    {
                        MelonLogger.Log(ConsoleColor.DarkYellow, $"Warning: could not find original dynamic bone info for {player.Key}'s bone {db.gameObject.name} . This means his bones won't be disabled!");
                    }
                }
            }
        }
    }

    public static class ListExtensions
    {
        public static T[] ToArrayExtension<T>(this Il2CppSystem.Collections.Generic.List<T> list)
        {
            T[] arr = new T[list.Count];
            for (int x = 0; x < list.Count; x++)
            {
                arr[x] = list[x];
            }
            return arr;
        }
    }
}
