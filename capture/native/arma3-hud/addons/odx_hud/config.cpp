class CfgPatches
{
    class ODX_HUD
    {
        name = "ODX HUD Bridge";
        author = "ODX";
        requiredVersion = 2.12;
        requiredAddons[] = {"A3_UI_F"};
        units[] = {};
        weapons[] = {};
        version = "0.3.4";
    };
};

class CfgFunctions
{
    class ODX
    {
        tag = "ODX";
        class HUD
        {
            file = "\z\odx\addons\hud\functions";
            class postInit
            {
                postInit = 1;
            };
        };
    };
};
