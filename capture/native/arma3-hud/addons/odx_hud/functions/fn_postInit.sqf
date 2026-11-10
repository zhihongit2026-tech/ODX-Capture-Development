/*
    ODX HUD Bridge 0.3.4
    This script only reports telemetry and applies explicit ODX commands.
    It does not create UI controls, hints, overlays or notification sounds.
*/
if (!hasInterface || isDedicated) exitWith {};
if (missionNamespace getVariable ["ODX_HUD_Initialized", false]) exitWith {};
missionNamespace setVariable ["ODX_HUD_Initialized", true];

[] spawn {
    waitUntil { !isNull player && !isNull findDisplay 46 };

    private _hudHidden = false;
    private _missionBlocksHud = isArray (missionConfigFile >> "showHUD");
    private _lastBridgeError = 0;

    private _makeSnapshot = {
        private _tacticalPing = "UNVERIFIED";
        if !(isNil { difficultyOption "tacticalPing" }) then { _tacticalPing = str (difficultyOption "tacticalPing"); };
        private _difficulty = [
            ["reducedDamage", difficultyOption "reducedDamage"],
            ["groupIndicators", difficultyOption "groupIndicators"],
            ["friendlyTags", difficultyOption "friendlyTags"],
            ["enemyTags", difficultyOption "enemyTags"],
            ["detectedMines", difficultyOption "detectedMines"],
            ["commands", difficultyOption "commands"],
            ["waypoints", difficultyOption "waypoints"],
            ["weaponInfo", difficultyOption "weaponInfo"],
            ["stanceIndicator", difficultyOption "stanceIndicator"],
            ["staminaBar", difficultyOption "staminaBar"],
            ["weaponCrosshair", difficultyOption "weaponCrosshair"],
            ["visionAid", difficultyOption "visionAid"],
            ["thirdPersonView", difficultyOption "thirdPersonView"],
            ["cameraShake", difficultyOption "cameraShake"],
            ["scoreTable", difficultyOption "scoreTable"],
            ["deathMessages", difficultyOption "deathMessages"],
            ["vonID", difficultyOption "vonID"],
            ["mapContent", difficultyOption "mapContent"],
            ["autoReport", difficultyOption "autoReport"],
            ["multipleSaves", difficultyOption "multipleSaves"],
            ["tacticalPing", _tacticalPing]
        ];

        private _video = getVideoOptions;
        private _videoKeys = keys _video;
        _videoKeys = _videoKeys select { !(_x in ["vramInfo", "monitorResolution"]) };
        _videoKeys sort true;
        private _videoSettings = _videoKeys apply { [_x, _video get _x] };

        private _mods = +activatedAddons;
        _mods sort true;
        private _resolution = getResolution;
        private _version = productVersion;

        createHashMapFromArray [
            ["gameTitle", "Arma 3"],
            ["gameVersion", str _version],
            ["modFingerprint", str _mods],
            ["settingsFingerprint", str [_difficulty, _videoSettings]],
            ["screenWidth", _resolution select 0],
            ["screenHeight", _resolution select 1],
            ["aspectRatio", (_resolution select 0) / (_resolution select 1)],
            ["hudBridgeVersion", "0.3.4"],
            ["hudBridgeReady", true],
            ["hudHidden", _hudHidden],
            ["missionBlocksHud", _missionBlocksHud],
            ["settings", [_difficulty, _videoSettings]]
        ]
    };

    private _snapshot = call _makeSnapshot;
    "ODX_HUD" callExtension ["report", [toJSON _snapshot]];

    while { true } do {
        if (isNull player) exitWith {};

        private _telemetry = createHashMapFromArray [
            ["gameFps", diag_fps],
            ["gameFpsMin", diag_fpsMin],
            ["sampledAtUtcMs", (diag_tickTime * 1000)]
        ];

        private _reply = "ODX_HUD" callExtension ["poll", [toJSON _telemetry]];
        private _command = if (_reply isEqualType [] && { count _reply > 0 }) then { toUpper (_reply select 0) } else { "" };

        switch (_command) do {
            case "HIDE": {
                if (_missionBlocksHud) {
                    _hudHidden = false;
                    "ODX_HUD" callExtension ["report", [toJSON (call _makeSnapshot)]];
                } else {
                    showHUD false;
                    _hudHidden = true;
                    "ODX_HUD" callExtension ["ack", ["HIDDEN"]];
                };
            };
            case "RESTORE": {
                showHUD true;
                _hudHidden = false;
                "ODX_HUD" callExtension ["ack", ["RESTORED"]];
            };
            case "PREFLIGHT": {
                _snapshot = call _makeSnapshot;
                "ODX_HUD" callExtension ["report", [toJSON _snapshot]];
            };
        };

        if (_reply isEqualType [] && { count _reply > 0 } && { (_reply select 0) == "NO_BRIDGE" }) then {
            if (diag_tickTime - _lastBridgeError > 30) then {
                diag_log "[ODX_HUD] ODX desktop bridge is not running.";
                _lastBridgeError = diag_tickTime;
            };
        };

        uiSleep 1;
    };
};
