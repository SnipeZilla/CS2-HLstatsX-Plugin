using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;

namespace HLstatsZ;

public static class CCSPlayerControllerExtensions
{
    public static void Freeze(this CCSPlayerController player, int mode = 2)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null) return;

        switch (mode)
        {
            case 1: pawn.ChangeMovetype(MoveType_t.MOVETYPE_OBSOLETE); break;
            case 2: pawn.ChangeMovetype(MoveType_t.MOVETYPE_NONE); break;
            case 3: pawn.ChangeMovetype(MoveType_t.MOVETYPE_INVALID); break;
            default: pawn.ChangeMovetype(MoveType_t.MOVETYPE_NONE); break;
        }
    }

    public static void UnFreeze(this CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null) return;

        pawn.ChangeMovetype(MoveType_t.MOVETYPE_WALK);
    }

    private static void ChangeMovetype(this CBasePlayerPawn pawn, MoveType_t movetype)
    {
        pawn.MoveType = movetype;
        Schema.SetSchemaValue(
            pawn.Handle, "CBaseEntity", "m_nActualMoveType", movetype
        );
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }
}
