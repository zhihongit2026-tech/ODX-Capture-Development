class CfgPatches
{
    class ODX_HUD
    {
        name = "ODX HUD Bridge";
        author = "ODX Capture";
        requiredVersion = 2.10;
        requiredAddons[] = {"A3_Functions_F"};
        units[] = {};
        weapons[] = {};
    };
};

class CfgFunctions
{
    class ODX
    {
        tag = "ODX";
        class HUD
        {
            file = "\\odx_hud\\functions";
            class setHud {};
            class postInit
            {
                postInit = 1;
            };
        };
    };
};
