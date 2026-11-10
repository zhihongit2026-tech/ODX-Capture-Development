if (!hasInterface) exitWith {};
if (missionNamespace getVariable ["ODX_HUD_booted", false]) exitWith {};

ODX_HUD_booted = true;
ODX_HUD_active = false;
ODX_HUD_original = [];
ODX_HUD_version = "1.0.0";

0 spawn
{
    private _lastDisplay = displayNull;
    while { true } do
    {
        private _display = findDisplay 46;
        if (!isNull _display && { _display != _lastDisplay }) then
        {
            _display displayAddEventHandler ["KeyDown",
            {
                params ["_display", "_key"];
                switch (_key) do
                {
                    case 0x7C: { [true] call ODX_fnc_setHud; false };  // F13
                    case 0x7D: { [false] call ODX_fnc_setHud; false }; // F14
                    default { false };
                };
            }];
            _lastDisplay = _display;
            diag_log "ODX_HUD|ACK|READY";
        };
        sleep 1;
    };
};

0 spawn
{
    while { true } do
    {
        if (missionNamespace getVariable ["ODX_HUD_active", false]) then
        {
            if (isArray (missionConfigFile >> "showHUD")) then
            {
                ODX_HUD_active = false;
                diag_log "ODX_HUD|ACK|BLOCKED|MISSION_SHOWHUD_ARRAY";
            }
            else
            {
                showHUD [false, false, false, false, false, false, false, false, false, false, false];
                showCompass false;
                showGPS false;
                showWatch false;
                showUAVFeed false;
                showPad false;
                showRadio false;
                showScoretable false;
                showSubtitles false;
                showCuratorCompass false;
                showWarrant false;
                showChat false;
            };
        };
        sleep 0.25;
    };
};

addMissionEventHandler ["MPEnded", { [false] call ODX_fnc_setHud; }];
addMissionEventHandler ["Ended", { [false] call ODX_fnc_setHud; }];

0 spawn
{
    private _difficultyNames = ["groupIndicators", "friendlyTags", "enemyTags", "detectedMines", "commands", "waypoints"];
    while { true } do
    {
        private _resolution = getResolution;
        private _difficulty = _difficultyNames apply { try { difficultyOption _x } catch { -1 } };
        private _tacticalPing = try { difficultyOption "tacticalPing" } catch { -1 };
        private _addons = activatedAddons;
        _addons sort true;
        private _modsHash = hashValue (str _addons);
        private _missionOverride = isArray (missionConfigFile >> "showHUD");
        private _hudVisible =
            shownHUD || shownCompass || shownGPS || shownWatch || shownUAVFeed ||
            shownPad || shownRadio || shownScoretable || shownSubtitles ||
            shownCuratorCompass || shownWarrant || shownChat;

        diag_log format [
            "ODX_HUD|T|%1|%2|%3|%4|%5|%6|%7|%8|%9|%10|%11|%12|%13|%14|%15|%16|%17",
            ODX_HUD_version, diag_fps, diag_fpsMin,
            _resolution # 0, _resolution # 1, _resolution # 2,
            _hudVisible, _missionOverride,
            _difficulty # 0, _difficulty # 1, _difficulty # 2,
            _difficulty # 3, _difficulty # 4, _difficulty # 5,
            _tacticalPing, _modsHash, ODX_HUD_active
        ];
        sleep 1;
    };
};
