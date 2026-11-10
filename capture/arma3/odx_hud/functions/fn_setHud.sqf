params [["_hide", true, [true]]];

if (!hasInterface) exitWith { false };

if (_hide) exitWith
{
    // A mission-defined showHUD[] array disables the scripting command entirely.
    if (isArray (missionConfigFile >> "showHUD")) exitWith
    {
        diag_log "ODX_HUD|ACK|BLOCKED|MISSION_SHOWHUD_ARRAY";
        false
    };

    if (isNil "ODX_HUD_original" || { count ODX_HUD_original == 0 }) then
    {
        ODX_HUD_original = [
            shownHUD,
            shownCompass,
            shownGPS,
            shownWatch,
            shownUAVFeed,
            shownPad,
            shownRadio,
            shownScoretable,
            shownSubtitles,
            shownCuratorCompass,
            shownWarrant,
            shownChat
        ];
    };

    // All 11 showHUD groups are disabled, including scripted HUD, Action Menu,
    // weapon cursors, radar, compass, command/group info, panels, kill messages
    // and 3D icons.
    showHUD [false, false, false, false, false, false, false, false, false, false, false];

    // Disable map indicators, including tactical pings, for the capture window.
    disableMapIndicators [true, true, true, true];

    // Additional Player UI switches are separate from the 11 showHUD groups.
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

    ODX_HUD_active = true;
    diag_log "ODX_HUD|ACK|HIDDEN";
    true
};

if (ODX_HUD_active) then
{
    private _original = missionNamespace getVariable ["ODX_HUD_original", []];

    // shownHUD returns the exact 11-element state; restore it rather than guessing.
    if (count _original > 0 && { (_original # 0) isEqualType [] } && { count (_original # 0) == 11 }) then
    {
        showHUD (_original # 0);
    };

    // Restore map indicators after the temporary capture override.
    disableMapIndicators [false, false, false, false];

    if (count _original > 1) then { showCompass (_original # 1); };
    if (count _original > 2) then { showGPS (_original # 2); };
    if (count _original > 3) then { showWatch (_original # 3); };
    if (count _original > 4) then { showUAVFeed (_original # 4); };
    if (count _original > 5) then { showPad (_original # 5); };
    if (count _original > 6) then { showRadio (_original # 6); };
    if (count _original > 7) then { showScoretable (_original # 7); };
    if (count _original > 8) then { showSubtitles (_original # 8); };
    if (count _original > 9) then { showCuratorCompass (_original # 9); };
    if (count _original > 10) then { showWarrant (_original # 10); };
    if (count _original > 11) then { showChat (_original # 11); };

    ODX_HUD_active = false;
    ODX_HUD_original = [];
    diag_log "ODX_HUD|ACK|RESTORED";
};

true
