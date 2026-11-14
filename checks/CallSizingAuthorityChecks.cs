// Supplied native service/state fixture for the server resize transaction.
// Does not establish transport/reach authentication or a real dedicated world.
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Core.Services;
using HarmonyLib;
using StellarFunctions.Content;
using StellarFunctions.Game;
using Ships;
using Ships.Cables;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Placement;
using Ships.Interface.Settings;
using Ships.Interface.Services;
using Ships.Parts.Common.Model;
using Ships.Physics;
using Ships.Physics.Collision;
using Ships.Services;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StellarFunctions;
internal static class CallSizingAuthorityChecks
{
    internal static void Run(Action<bool,string> check)
    {
        const uint shipId=491001;const ushort partId=41;
        Type sizing=typeof(FunctionCallState).Assembly.GetType("StellarFunctions.CallSizing");
        MethodInfo handle=AccessTools.Method(sizing,"Handle"),payload=AccessTools.Method(sizing,"Payload"),variant=AccessTools.Method(sizing,"Variant");
        var services=new GameObject("Private Call resize authority services");services.tag="ServiceObject";
        var ships=services.AddComponent<ShipsServerTracker>();var connections=services.AddComponent<ConnectionsServerTracker>();
        var state=new ShipState(shipId,shipId,new PartSettingsProvider());
        var ship=(TrackedShipServer)FormatterServices.GetUninitializedObject(typeof(TrackedShipServer));
        AccessTools.Field(typeof(TrackedShip),"<State>k__BackingField").SetValue(ship,state);
        ((IDictionary)AccessTools.Field(typeof(ShipsServerTracker),"_shipsDict").GetValue(ships)).Add(shipId,ship);
        var part=new StatefulPart(partId,Support.Registration.Get(62005),new FloatingPartPlacement(Vector3.zero,PartOrientation.Forward,PartOrientation.Up))
        {State=new FunctionCallState{IsInUse=true,SelectedFunctionId=Guid.NewGuid().ToString("N"),Slot0=Guid.NewGuid().ToString("N"),Slot1=Guid.NewGuid().ToString("N"),Slot2=Guid.NewGuid().ToString("N"),RuntimeJson="{\"kept\":9}",StructureKey="kept"}};
        state.AddStatefulPartWithoutInit(part);
        var original=(FunctionCallState)part.State;
        string Request(int count)=>(string)payload.Invoke(null,new object[]{(FunctionCallState)part.State,count});
        void Act(string request)=>handle.Invoke(null,new object[]{ship,part,(FunctionCallState)part.State,request});
        var scene=SceneManager.CreateScene("Private Call resize clearance",new CreateSceneParameters(LocalPhysicsMode.Physics3D));
        GameObject root=null,obstacle=null,interactionRoot=null,solidRoot=null,instancerRoot=null;
        try
        {
            check(ReferenceEquals(ServiceLocator.GetService<ShipsServerTracker>(),ships)&&ReferenceEquals(ServiceLocator.GetService<ConnectionsServerTracker>(),connections),"resize fixture resolves exact native services without a world");
            new CallPartBehaviour().OnAction(new ContentPart{ShipId=shipId,PartId=partId,Part=part},null,Request(1));
            check(ReferenceEquals(part.State,original),"real Call action refuses a missing authenticated player before resizing");
            Act("resize-call:"+Guid.NewGuid().ToString("N")+":3:1");
            check(((FunctionCallState)part.State).InputCount==3&&!string.IsNullOrEmpty(((FunctionCallState)part.State).ResizeFeedback),"resize transaction refuses a forged instance identity");
            Act("resize-call:"+original.InstanceId.ToString("N")+":2:1");
            check(((FunctionCallState)part.State).InputCount==3,"resize transaction refuses stale expected size");
            foreach(byte terminal in new byte[]{0,1,2,3})
            {
                var edge=new SocketConnection(new EntityPartSocket(shipId,partId,terminal),new EntityPartSocket(shipId,999,0),SocketConnectionType.Cable);
                connections.SignalConnectionGroups.OnConnectionCreate(edge);
                Act(Request(1));
                check(((FunctionCallState)part.State).InputCount==3&&((FunctionCallState)part.State).ResizeFeedback.Contains("Disconnect all terminals"),"resize rejects a wire on terminal "+terminal+" including output3");
                check(connections.IsSocketPhysicallyOccupied(new EntityPartSocket(shipId,partId,terminal)),"rejected resize preserves the terminal "+terminal+" connection");
                connections.SignalConnectionGroups.OnConnectionDelete(edge);
            }
            root=new GameObject("Private supplied ship interior collision root");SceneManager.MoveGameObjectToScene(root,scene);
            AccessTools.Field(typeof(TrackedShip),"_interiorColliders").SetValue(ship,root);
            AccessTools.Field(typeof(TrackedShip),"<IsInteriorColliderObjectCreated>k__BackingField").SetValue(ship,true);
            bool nativeLayoutFound=false;ShipHullColliderChunk chunkPrefab=null;
            foreach(var prefabs in Resources.FindObjectsOfTypeAll<Ships.Values.ShipPrefabs>())
            {
                if(prefabs.shipColliders==null)continue;
                nativeLayoutFound=true;
                check(prefabs.shipColliders.GetComponentInChildren<ShipInteractionColliders>(true)==null,
                    "native dedicated interior prefab has solid geometry, not the supplied interaction hierarchy");
                var hull=prefabs.shipColliders.GetComponent<ShipHullColliders>();
                chunkPrefab=(ShipHullColliderChunk)AccessTools.Field(typeof(ShipHullColliders),"chunkColliderPrefab").GetValue(hull);
            }
            check(nativeLayoutFound,"native ship collider prefab was inspected rather than assuming the supplied hierarchy");
            interactionRoot=new GameObject("Native mounted Call interactions");interactionRoot.transform.SetParent(root.transform,false);
            var nativeInteractions=interactionRoot.AddComponent<ShipInteractionColliders>();nativeInteractions.Setup(ship);
            state.HookPartListeners(interactionRoot);
            foreach(var collider in interactionRoot.GetComponentsInChildren<Collider>(true))
                check(true,"native interaction collider "+collider.name+" layer="+LayerMask.LayerToName(collider.gameObject.layer)+" trigger="+collider.isTrigger);
            instancerRoot=new GameObject("Native clearance instancer");
            var instancer=instancerRoot.AddComponent<Ships.Visuals.ShipPartInstancer>();
            solidRoot=new GameObject("Native clearance solid colliders");solidRoot.transform.SetParent(root.transform,false);
            var solids=solidRoot.AddComponent<ShipStatefulColliders>();
            AccessTools.Field(typeof(ShipStatefulColliders),"collidersParent").SetValue(solids,solidRoot.transform);
            AccessTools.Field(typeof(ShipStatefulColliders),"_shipPartInstancer").SetValue(solids,instancer);
            state.HookPartListeners(solidRoot);
            // Use the shipped plane scanner AND chunk collider spawning method,
            // including its 100 mm wall thickness. The saved mount offset is
            // 40 mm from the plane center: normal existing contact is 10 mm.
            foreach(bool wall in new[]{false,true})
            {
                var placement=new HullPartPlacement(Vector3Int.zero,wall?VoxelPartType.ForwardWall:VoxelPartType.Floor,
                    wall?PartOrientation.Forward:PartOrientation.Up,wall?PartOrientation.Up:PartOrientation.Forward);
                var plane=new ColliderPlane(ColliderPlaneConfig.GetId(placement,Vector3Int.zero));
                plane.Parts.Add(new ColliderPlanePart(placement));
                var chunk=UnityEngine.Object.Instantiate(chunkPrefab,root.transform);
                foreach(var shape in new PlaneColliderScanner().GetColliders(Vector3Int.zero,plane))
                    AccessTools.Method(typeof(ShipHullColliderChunk),"SpawnCollider").Invoke(chunk,new object[]{shape,placement.Position.Type,placement.ForwardOrientation});
                ushort mountedId=(ushort)(wall?202:201);
                var mounted=new StatefulPart(mountedId,Support.Registration.Get(62005),new FloatingPartPlacement(
                    wall?new Vector3(0,0,.46f):new Vector3(0,-.46f,0),wall?PartOrientation.Up:PartOrientation.Forward,
                    wall?PartOrientation.Back:PartOrientation.Up)){State=original with{InstanceId=Guid.NewGuid(),InputCount=3}};
                state.AddStatefulPartWithoutInit(mounted);
                void ResizeMounted(int size)=>handle.Invoke(null,new object[]{ship,mounted,(FunctionCallState)mounted.State,
                    (string)payload.Invoke(null,new object[]{(FunctionCallState)mounted.State,size})});
                foreach(int size in new[]{1,2,3,2,1,3})
                {
                    ResizeMounted(size);
                    check(((FunctionCallState)mounted.State).InputCount==size,
                        "native "+(wall?"wall":"floor")+" with saved 10 mm support contact allows resize to "+size+": "+((FunctionCallState)mounted.State).ResizeFeedback);
                }
                var newHull=new GameObject("New hull obstacle outside old footprint");newHull.transform.SetParent(chunk.transform,false);
                newHull.transform.SetLocalPositionAndRotation(mounted.LocalPosition,mounted.LocalRotation);
                var newHullBox=newHull.AddComponent<BoxCollider>();newHullBox.center=new Vector3(-.215f,.075f,0);newHullBox.size=new Vector3(.012f,.05f,.05f);
                ResizeMounted(1);
                check(((FunctionCallState)mounted.State).InputCount==3,"support exemption does not allow a previously untouched hull obstacle on "+(wall?"wall":"floor"));
                UnityEngine.Object.DestroyImmediate(newHull);
                var ceiling=new GameObject("Ceiling is not backing support");ceiling.transform.SetParent(chunk.transform,false);
                ceiling.transform.SetLocalPositionAndRotation(mounted.LocalPosition+mounted.LocalRotation*new Vector3(0,.19f,0),mounted.LocalRotation*Quaternion.Euler(90,0,0));
                ceiling.AddComponent<BoxCollider>().size=new Vector3(1,1,.1f);
                ResizeMounted(1);
                check(((FunctionCallState)mounted.State).InputCount==3,"existing ceiling contact cannot be exempted as bottom support on "+(wall?"wall":"floor"));
                UnityEngine.Object.DestroyImmediate(ceiling);
            }
            Act(Request(1));
            check(((FunctionCallState)part.State).InputCount==1,"native Call interaction and socket colliders do not block mounted 3-to-1 resize: "+((FunctionCallState)part.State).ResizeFeedback);
            state.SetPartState(partId,original);
            Bounds largeBounds=part.Settings.Bounds,smallBounds=((PartSettings)variant.Invoke(null,new object[]{1})).Bounds;
            check(true,"resize diagnostic current="+largeBounds.ToString("F6")+" requested="+smallBounds.ToString("F6"));
            // A nearby player's collision volume (or existing mount contact)
            // occupies only space already occupied by this Call. It must not
            // prevent removing the outer inputs, even with plenty of free room.
            obstacle=new GameObject("Existing footprint contact");SceneManager.MoveGameObjectToScene(obstacle,scene);
            var contact=obstacle.AddComponent<BoxCollider>();contact.center=smallBounds.center;contact.size=new Vector3(.04f,.04f,.04f);
            Act(Request(1));var small=(FunctionCallState)part.State;
            check(small.InputCount==1&&small.ResizeFeedback=="","unwired Call can shrink to one input despite contact in its existing footprint: "+small.ResizeFeedback);
            UnityEngine.Object.DestroyImmediate(obstacle);obstacle=null;
            check(small.IsInUse&&small.InstanceId==original.InstanceId&&small.SelectedFunctionId==original.SelectedFunctionId&&small.Slot0==original.Slot0&&small.Slot1==original.Slot1&&small.Slot2==original.Slot2&&small.RuntimeJson==original.RuntimeJson&&small.StructureKey==original.StructureKey,"resize preserves live interaction, selection, binding identities and private Memory");
            AccessTools.Field(typeof(TrackedShip),"<IsInteriorColliderObjectCreated>k__BackingField").SetValue(ship,false);
            Act(Request(2));
            check(((FunctionCallState)part.State).InputCount==1&&((FunctionCallState)part.State).ResizeFeedback.Contains("collision geometry"),"expansion fails closed until native ship collision geometry is available");
            AccessTools.Field(typeof(TrackedShip),"<IsInteriorColliderObjectCreated>k__BackingField").SetValue(ship,true);
            var own=new GameObject("Current Call solid collider");own.transform.SetParent(root.transform,false);
            own.AddComponent<ShipPartIdentifier>().Id=partId;var ownBox=own.AddComponent<BoxCollider>();Bounds b=((PartSettings)variant.Invoke(null,new object[]{1})).Bounds;ownBox.center=b.center;ownBox.size=b.size;
            obstacle=new GameObject("Blocking neighbor");SceneManager.MoveGameObjectToScene(obstacle,scene);
            obstacle.transform.position=new Vector3(.16f,.075f,0);obstacle.AddComponent<BoxCollider>().size=new Vector3(.08f,.1f,.1f);
            Act(Request(2));
            check(((FunctionCallState)part.State).InputCount==1&&((FunctionCallState)part.State).ResizeFeedback.Contains("clear space"),"overlapping neighbor refuses expansion and retains original model");
            UnityEngine.Object.DestroyImmediate(obstacle);obstacle=null;
            Act(Request(2));check(((FunctionCallState)part.State).InputCount==2,"clear expansion accepts size2 and ignores the Call's own solid collider");
            Act(Request(3));check(((FunctionCallState)part.State).InputCount==3,"clear expansion accepts size3 while preserving mounting position");
            check(part.LocalPosition==Vector3.zero&&((FunctionCallState)part.State).RuntimeJson==original.RuntimeJson,"resize clearance checks preserve mount and private state");

            // The stock small housing genuinely extends a little to the left
            // of the stretched original. A shrink still cannot enter NEW solid
            // space; ignoring every lower-count edit would hide this collision.
            obstacle=new GameObject("Obstacle in newly occupied left strip");SceneManager.MoveGameObjectToScene(obstacle,scene);
            var strip=obstacle.AddComponent<BoxCollider>();
            strip.center=new Vector3((smallBounds.min.x+largeBounds.min.x)*.5f,.075f,0);
            strip.size=new Vector3((largeBounds.min.x-smallBounds.min.x)*.6f,.05f,.05f);
            Act(Request(1));
            check(((FunctionCallState)part.State).InputCount==3&&((FunctionCallState)part.State).ResizeFeedback.Contains("clear space"),"shrink rejects a genuine obstacle in the small housing's new left strip");
            UnityEngine.Object.DestroyImmediate(obstacle);obstacle=null;

            var mount=new GameObject("Touching mounting surface");mount.transform.SetParent(root.transform,false);
            mount.transform.localPosition=new Vector3(0,-.05f,0);mount.AddComponent<BoxCollider>().size=new Vector3(2,.1f,2);
            var existingContact=new GameObject("Shared footprint contact");existingContact.transform.SetParent(root.transform,false);
            var sharedBox=existingContact.AddComponent<BoxCollider>();sharedBox.center=new Vector3(-.15f,.075f,0);sharedBox.size=Vector3.one*.02f;
            foreach(var rotation in new[]{Quaternion.identity,Quaternion.Euler(0,90,0),Quaternion.Euler(90,0,90)})
            {
                root.transform.SetPositionAndRotation(new Vector3(5,7,-4),rotation);
                foreach(int from in new[]{1,2,3})foreach(int to in new[]{1,2,3})
                {
                    if(from==to)continue;
                    state.SetPartState(partId,original with{InputCount=from});
                    Bounds ownBounds=part.Settings.Bounds;ownBox.center=ownBounds.center;ownBox.size=ownBounds.size;
                    Act(Request(to));
                    check(((FunctionCallState)part.State).InputCount==to&&((FunctionCallState)part.State).ResizeFeedback=="",
                        "clear mounted resize "+from+"->"+to+" succeeds with existing contact at root rotation "+rotation.eulerAngles);
                }
                state.SetPartState(partId,original with{InputCount=1});
                obstacle=new GameObject("Rotated expansion obstruction");SceneManager.MoveGameObjectToScene(obstacle,scene);
                obstacle.transform.SetPositionAndRotation(root.transform.TransformPoint(new Vector3(.16f,.075f,0)),rotation);
                obstacle.AddComponent<BoxCollider>().size=new Vector3(.08f,.1f,.1f);
                Act(Request(2));
                check(((FunctionCallState)part.State).InputCount==1,"new expansion obstruction rejected at rotated/transformed ship root "+rotation.eulerAngles);
                UnityEngine.Object.DestroyImmediate(obstacle);obstacle=null;
            }

            // Vary part-local orientation as well as the containing root.
            int orientationIndex=0;
            foreach(var direction in new[]{PartOrientation.Forward,PartOrientation.Right,PartOrientation.Back,PartOrientation.Left})
            {
                ushort orientedId=(ushort)(partId+10+orientationIndex++);
                var oriented=new StatefulPart(orientedId,Support.Registration.Get(62005),new FloatingPartPlacement(new Vector3(3*orientationIndex,3,3),direction,PartOrientation.Up))
                    {State=original with{InstanceId=Guid.NewGuid(),InputCount=3}};
                state.AddStatefulPartWithoutInit(oriented);
                foreach(int count in new[]{1,2,3,1})
                {
                    handle.Invoke(null,new object[]{ship,oriented,(FunctionCallState)oriented.State,
                        (string)payload.Invoke(null,new object[]{(FunctionCallState)oriented.State,count})});
                    check(((FunctionCallState)oriented.State).InputCount==count,
                        "native solid plus interaction resize succeeds at part-local orientation "+direction+" to "+count);
                }
            }

            // Only the target's registered hierarchy is exempt: a foreign
            // registered interaction remains foreign, and solids still block.
            state.SetPartState(partId,original);
            var foreign=new StatefulPart(99,Support.Registration.Get(62005),new FloatingPartPlacement(new Vector3(-.5f,0,0),PartOrientation.Forward,PartOrientation.Up))
                {State=original with{InstanceId=Guid.NewGuid(),InputCount=3}};
            nativeInteractions.AddStatefulPart(foreign);
            Act(Request(1));
            check(((FunctionCallState)part.State).InputCount==3&&((FunctionCallState)part.State).ResizeFeedback.Contains("clear space"),
                "another part's native interaction hierarchy is not exempted as the target's own collider");

            // Issue #1: a Touching connection made under the old variant's
            // socket layout must not survive a resize that moves the sockets
            // apart. Connections are keyed by socket id, so the refresh must
            // delete stale Touching/RelayedByPart records before re-adding.
            var displaySettings=Resources.FindObjectsOfTypeAll<PartSettings>()
                .FirstOrDefault(p=>p!=null&&p.name=="SignalDisplay"&&p.part!=null);
            check(displaySettings!=null,"touching fixture finds the native SignalDisplay settings");
            var displaySocket=displaySettings.part.GetComponentsInChildren<SocketObject>(true)
                .First(s=>s!=null&&s.Type==CableType.Signal);
            var callThree=(PartSettings)variant.Invoke(null,new object[]{3});
            var callSocket3=callThree.part.GetComponentsInChildren<SocketObject>(true)
                .First(s=>s!=null&&s.Id==3&&s.Type==CableType.Signal);
            Vector3 SocketWorld(PartSettings settings,SocketObject socket,IPartPlacement placement)
            {
                var marker=socket.GetComponent<FunctionSocketMarker>();
                Vector3 local=marker!=null&&marker.Root!=null
                    ?marker.Root.InverseTransformPoint(socket.transform.position)
                    :socket.transform.position;
                return placement.LocalPosition+placement.Rotation*local;
            }
            var identity=new FloatingPartPlacement(Vector3.zero,PartOrientation.Forward,PartOrientation.Up);
            var callPlacement=(IPartPlacement)part.Placement;
            Vector3 callOut=SocketWorld(callThree,callSocket3,callPlacement);
            // Place the display so its socket world position equals the Call
            // output's: pos = callOut - R0*local, with R0 the shared frame.
            Vector3 displayPos=callOut-SocketWorld(displaySettings,displaySocket,identity);
            var displayPlacement=new FloatingPartPlacement(displayPos,PartOrientation.Forward,PartOrientation.Up);
            ushort displayId=88;
            var displayState=PartStateFactory.BuildDefaultState(displaySettings);
            var displayPart=new StatefulPart(displayId,displaySettings,displayPlacement){State=displayState};
            state.AddStatefulPartWithoutInit(displayPart);state.InitStatefulPart(displayId);
            var added=AccessTools.Method(typeof(ConnectionsTracker),"OnShipStatefulPartAdded");
            added.Invoke(connections,new object[]{shipId,(StatefulPart)part});
            added.Invoke(connections,new object[]{shipId,displayPart});
            var outSocket=new EntityPartSocket(shipId,partId,3);
            var inSocket=new EntityPartSocket(shipId,displayId,displaySocket.Id);
            check(connections.SignalConnectionGroups.TryGetSocketPhysicalConnection(outSocket,out var touching)&&
                touching.Type==SocketConnectionType.Touching&&
                ((touching.SocketA==outSocket&&touching.SocketB==inSocket)||(touching.SocketA==inSocket&&touching.SocketB==outSocket)),
                "coincident Call output and display input form a native Touching signal connection");
            state.SetPartState(partId,original with{InputCount=1});
            var callOne=(PartSettings)variant.Invoke(null,new object[]{1});
            Vector3 movedOut=SocketWorld(callOne,callOne.part.GetComponentsInChildren<SocketObject>(true)
                .First(s=>s!=null&&s.Id==3&&s.Type==CableType.Signal),callPlacement);
            check(Vector3.Distance(movedOut,callOut)>.05f,"one-input variant moves the output socket away from the display");
            bool stale=connections.SignalConnectionGroups.TryGetSocketConnections(outSocket,out var remaining)&&
                remaining.Any(c=>c.Type==SocketConnectionType.Touching&&
                    ((c.SocketA==outSocket&&c.SocketB==inSocket)||(c.SocketA==inSocket&&c.SocketB==outSocket)));
            check(!stale,"resize to one input removes the stale Touching connection once the sockets no longer coincide");
            state.SetPartState(partId,original);
        }
        finally
        {
            if(interactionRoot!=null){state.UnhookPartListeners(interactionRoot);UnityEngine.Object.DestroyImmediate(interactionRoot);}
            if(solidRoot!=null){state.UnhookPartListeners(solidRoot);UnityEngine.Object.DestroyImmediate(solidRoot);}
            if(instancerRoot!=null)UnityEngine.Object.DestroyImmediate(instancerRoot);
            if(obstacle!=null)UnityEngine.Object.DestroyImmediate(obstacle);
            if(root!=null)UnityEngine.Object.DestroyImmediate(root);
            SceneManager.UnloadSceneAsync(scene);UnityEngine.Object.DestroyImmediate(services);
        }
    }
}
