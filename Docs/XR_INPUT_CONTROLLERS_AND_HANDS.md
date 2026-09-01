# Controllers + hand tracking

The app can be driven with Touch controllers **or** bare hands. Thumbstick locomotion stays off —
the player never moves with the joystick.

## Why controllers did not work before

Two settings put the build into hands-only mode, which makes the Horizon OS runtime ignore the
controllers entirely:

| File | Was | Now |
| --- | --- | --- |
| `Assets/Oculus/OculusProjectConfig.asset` | `handTrackingSupport: 2` (`HandsOnly`) | `handTrackingSupport: 1` (`ControllersAndHands`) |
| `Assets/Plugins/Android/AndroidManifest.xml` | `oculus.software.handtracking` `required="true"` | `required="false"` |

`required="true"` on the hand-tracking feature is what tells the store/runtime "this app is hands
only". With `false`, hand tracking is still supported but controllers are allowed as well.

The third piece was the scene: the `[BuildingBlock] OVRInteractionComprehensive` prefab instance in
`PCAConversationalAISample.unity` ships with hand *and* controller interactor branches, but ~20 of
its children were deactivated when the project went hands-only.

## DualInputRigSetup

`Assets/Scripts/XRInput/DualInputRigSetup.cs` lives on `[BuildingBlock] Camera Rig` and repairs the
rig on load, so the prefab overrides do not have to be fixed by hand:

* activates every controller branch (`OVRControllers`, `ControllerInteractors`, controller visuals…)
* activates every hand-tracking branch (`OVRHands`, `HandInteractors…`)
* only enables the interactor *kinds* that are ticked in the inspector — ray + poke by default,
  grab and distance-grab off
* **disables** anything named like locomotion (`locomotion`, `teleport`, `turner`, `snapturn`,
  `joystick`, `thumbstick`, `movement`, …) plus any `CharacterController`, so the stick cannot move
  the player
* prints the whole rig hierarchy to the console before and after, with the active state of each
  object

It is additive: unticking an interactor kind leaves those objects exactly as the scene author set
them, it never switches something off (locomotion and the explicit disable list are the exceptions).

### If the rig's child names differ

The rules are keyword based, so they survive Meta renaming things between SDK versions, but if a
branch is missed:

1. Run **Tools ▸ Lerni ▸ XR ▸ Log Interaction Rig Tree** to print the real hierarchy.
2. Add the exact object name to `Extra Names To Enable` (or `Extra Names To Disable`) on the
   component.

**Tools ▸ Lerni ▸ XR ▸ Enable Controllers + Hands (apply to open scene)** applies the same rules in
the Editor and marks the scene dirty, so the result is baked into the scene file instead of being
re-applied at every load.

## Simultaneous hands and controllers

`OVRManager.SimultaneousHandsAndControllersEnabled` is deliberately left off. The app supports
*either* input at a time (put a controller down, hands take over), which is what the exam flow
needs. Turning it on would let one hand hold a controller while the other is tracked bare.
