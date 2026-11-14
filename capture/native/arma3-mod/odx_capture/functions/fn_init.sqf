// Passes render camera plus low-rate game telemetry to odx_capture_x64.dll.
// Camera fields remain unchanged; the appended telemetry is sampled at 2 Hz except menu state,
// which is refreshed every rendered frame so the menu guard reacts as quickly as the game renders.
if (!hasInterface) exitWith {};

ODX_guardSampleAt = -1;
ODX_guardSampleId = 0;
ODX_guardFps = 0;
ODX_guardPlayerPos = [0, 0, 0];
ODX_guardPlayerSpeed = 0;
ODX_guardPlayerAlive = 0;
ODX_guardTeammatesVisible = 0;
ODX_guardTeammatesTotal = 0;
ODX_guardEscPressed = false;
ODX_guardDisplay = displayNull;
ODX_guardKeyDownEh = -1;

addMissionEventHandler ["Draw3D", {
    // Display 46 is Arma 3's in-game mission display. Return false so ESC keeps its normal game behavior.
    private _display = findDisplay 46;
    if (!isNull _display && { isNull ODX_guardDisplay || { _display != ODX_guardDisplay } }) then {
        if (!isNull ODX_guardDisplay && { ODX_guardKeyDownEh >= 0 }) then {
            ODX_guardDisplay displayRemoveEventHandler ["KeyDown", ODX_guardKeyDownEh];
        };
        ODX_guardDisplay = _display;
        ODX_guardKeyDownEh = _display displayAddEventHandler ["KeyDown", {
            params ["_display", "_key"];
            if (_key isEqualTo 1) then { ODX_guardEscPressed = true; };
            false
        }];
    };

    private _origin = AGLToASL positionCameraToWorld [0, 0, 0];

    if (diag_tickTime - ODX_guardSampleAt >= 0.5) then {
        ODX_guardSampleAt = diag_tickTime;
        ODX_guardSampleId = ODX_guardSampleId + 1;
        ODX_guardFps = diag_fps;
        ODX_guardPlayerAlive = if (alive player) then { 1 } else { 0 };
        ODX_guardPlayerPos = getPosASL player;
        ODX_guardPlayerSpeed = if (ODX_guardPlayerAlive == 1) then {
            vectorMagnitude (velocity (vehicle player))
        } else {
            0
        };

        private _team = if (ODX_guardPlayerAlive == 1) then {
            allPlayers select {
                _x != player && { isPlayer _x } && { alive _x } && { side _x == side player }
            }
        } else {
            []
        };
        ODX_guardTeammatesTotal = count _team;
        ODX_guardTeammatesVisible = 0;
        {
            private _target = eyePos _x;
            private _screen = worldToScreen (ASLToAGL _target);
            private _onScreen = count _screen >= 2 &&
                { (_screen # 0) >= 0 } && { (_screen # 0) <= 1 } &&
                { (_screen # 1) >= 0 } && { (_screen # 1) <= 1 };
            private _clearLine = _onScreen &&
                { (lineIntersectsSurfaces [_origin, _target, player, _x, true, 1, "VIEW", "FIRE"]) isEqualTo [] };
            if (_clearLine) then { ODX_guardTeammatesVisible = ODX_guardTeammatesVisible + 1; };
        } forEach _team;
    };

    private _menu = commandingMenu;
    private _menuText = toString ((toArray _menu) select { _x != 44 && _x != 10 && _x != 13 });
    private _actionMenu = if (isActionMenuVisible) then { "1" } else { "0" };

    private _fields = [cameraView];
    _fields append (_origin apply { _x toFixed 4 });
    {
        _fields append (((AGLToASL positionCameraToWorld _x) vectorDiff _origin) apply { _x toFixed 6 });
    } forEach [[1, 0, 0], [0, 1, 0], [0, 0, 1]];
    _fields pushBack ((getResolution select 6) toFixed 6);
    _fields pushBack ((getObjectFOV cameraOn) toFixed 6);

    // Appended columns: game_fps, player ASL xyz, speed m/s, commanding menu, action menu,
    // visible teammates, same-side human teammates, player alive, telemetry sample id.
    _fields pushBack (ODX_guardFps toFixed 2);
    _fields append (ODX_guardPlayerPos apply { _x toFixed 3 });
    _fields pushBack (ODX_guardPlayerSpeed toFixed 3);
    _fields pushBack _menuText;
    _fields pushBack _actionMenu;
    _fields pushBack str ODX_guardTeammatesVisible;
    _fields pushBack str ODX_guardTeammatesTotal;
    _fields pushBack str ODX_guardPlayerAlive;
    _fields pushBack str ODX_guardSampleId;
    _fields pushBack (if (ODX_guardEscPressed) then { "1" } else { "0" });

    "odx_capture" callExtension (_fields joinString ",");
    ODX_guardEscPressed = false;
}];
