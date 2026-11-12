using System.Collections.Generic;
using DataViz.Networking;
using UnityEngine;
using Col = DataViz.Study.SelfTestKit.Col;
using Model = DataViz.MenuUI.LayersPanelModel;

namespace DataViz.Study
{
    /// <summary>
    /// Pilot 2 follow-ups on the general CUBIA branch. PF-01: a per-layer point size that does not
    /// change the active layer, logged with the resized layer's name and undoable on the dashboard.
    /// </summary>
    public static class PilotFollowUpSelfTests
    {
        const string k_Moles = "pf_moles.cdataset";
        const string k_Movement = "pf_movement.cdataset";

        static DatasetColumnar Moles() => SelfTestKit.Dataset(k_Moles,
            Col.Num("MoleX", 0.2f, 0.8f, 1.2f),
            Col.Num("MoleY", 1.1f, 1.3f, 1.5f),
            Col.Num("SpawnTime", 0.5f, 1.5f, 2.5f));

        static DatasetColumnar Movement() => SelfTestKit.Dataset(k_Movement,
            Col.Num("LaserX", 0f, 0.5f, 1f, 1.5f),
            Col.Num("LaserY", 1f, 1.2f, 1.4f, 1.6f),
            Col.Num("Time", 0f, 1f, 2f, 3f));

        // ------------------------------------------------------------------ PF-01

        [SelfTest("PF-01: size steps")]
        static void SizeSteps(SelfTestTally t)
        {
            float max = MultiplayerScatterplotManager.MaxPointSize;
            t.Near("up from 20%", 0.25f * max, Model.StepPointSize(0.2f * max, +1));
            t.Near("down from 20%", 0.16f * max, Model.StepPointSize(0.2f * max, -1));
            t.Near("down from between steps", 0.2f * max, Model.StepPointSize(0.21f * max, -1));
            t.Near("up from between steps", 0.25f * max, Model.StepPointSize(0.21f * max, +1));
            t.Near("top stays", max, Model.StepPointSize(max, +1));
            t.Near("bottom stays", 0.01f * max, Model.StepPointSize(0.01f * max, -1));
            t.Near("below bottom: up to first step", 0.01f * max, Model.StepPointSize(0f, +1));
            t.Near("direction 0 stays", 0.123f, Model.StepPointSize(0.123f, 0));
            t.Eq("percent text", "20%", Model.SizePercentText(0.2f * max));
            t.Eq("log text", "0.025", DashboardLayersSection.SizeText(0.025f));
        }

        [SelfTest("PF-01: resize a non-active layer")]
        static void ResizeNonActive(SelfTestTally t)
        {
            DatasetColumnar moles = Moles(), movement = Movement();
            SelfTestKit.WithManager(m =>
            {
                m.RequestLoadDatasetRpc(k_Moles);
                string molesId = m.ActiveLayerId;
                m.RequestAddDatasetLayerRpc(k_Movement);
                string movementId = m.ActiveLayerId;
                t.True("movement active", movementId != molesId);
                float movementSize = m.Stack.Find(movementId).PointSize;
                float activeMapSize = m.PointSize;

                var events = new List<string>();
                m.OnLayersChanged += () => events.Add("L");
                m.OnPlotSettingsChanged += () => events.Add("P");
                PlotSnapshot before = PlotSnapshot.Capture(m);

                float bigger = Model.StepPointSize(m.Stack.Find(molesId).PointSize, +1);
                if (bigger == m.Stack.Find(molesId).PointSize) bigger = Model.StepPointSize(bigger, -1); // at the top: go down instead
                float oldSize = m.Stack.Find(molesId).PointSize;
                m.RequestSetLayerPointSizeRpc(molesId, bigger);

                t.Near("moles resized", bigger, m.Stack.Find(molesId).PointSize);
                t.Near("movement unchanged", movementSize, m.Stack.Find(movementId).PointSize);
                t.Eq("active layer unchanged", movementId, m.ActiveLayerId);
                t.Near("Map slider value (active layer) unchanged", activeMapSize, m.PointSize);
                t.Eq("events", "L,P", string.Join(",", events));

                List<LayerChange> rows = PlotStateDiff.DescribeStack(before, PlotSnapshot.Capture(m), DatasetRegistry.Get);
                t.Eq("one log row", 1, rows.Count);
                if (rows.Count == 1)
                {
                    t.Eq("row action", StudyActions.SetPointSize, rows[0].Action);
                    t.Eq("row layer", m.Stack.Find(molesId).Name, rows[0].Layer);
                    t.Eq("row old (as the dashboard writes it)", DashboardLayersSection.SizeText(oldSize), rows[0].OldValue);
                    t.Eq("row new (as the dashboard writes it)", DashboardLayersSection.SizeText(bigger), rows[0].NewValue);
                }

                events.Clear();
                m.RequestSetLayerPointSizeRpc(molesId, bigger);
                t.Eq("same size: no events", 0, events.Count);
                m.RequestSetLayerPointSizeRpc(molesId, float.NaN);
                t.Near("NaN ignored", bigger, m.Stack.Find(molesId).PointSize);
                m.RequestSetLayerPointSizeRpc(molesId, 5f);
                t.Near("clamped to MaxPointSize", MultiplayerScatterplotManager.MaxPointSize, m.Stack.Find(molesId).PointSize);
                m.RequestSetLayerPointSizeRpc("L99", 0.05f);
                t.Near("unknown id dropped", MultiplayerScatterplotManager.MaxPointSize, m.Stack.Find(molesId).PointSize);

                // The forwarded op (client -> host) lands on the named layer too.
                t.Eq("network op applied", true,
                    NetworkScatterplotState.ApplyRequest(m, new PlotRequest(PlotOp.SetLayerPointSize, floatArg: 0.03f, layer: molesId)));
                t.Near("network op: moles", 0.03f, m.Stack.Find(molesId).PointSize);
                t.Eq("network op: active unchanged", movementId, m.ActiveLayerId);

                // Computed layers draw lines: refused, nothing changes.
                m.RequestAddComputedLayerRpc(movementId, ComputedLayerKind.Path);
                string computedId = m.ActiveLayerId;
                float computedSize = m.Stack.Find(computedId).PointSize;
                string message = null;
                m.OnPlotMessage += (text, kind) => message = text;
                m.RequestSetLayerPointSizeRpc(computedId, 0.07f);
                t.Near("computed refused", computedSize, m.Stack.Find(computedId).PointSize);
                t.True("computed: refusal message", !string.IsNullOrEmpty(message));
            }, (k_Moles, moles), (k_Movement, movement));
        }

        [SelfTest("PF-01: dashboard undo of a layer size")]
        static void UndoSize(SelfTestTally t)
        {
            DatasetColumnar moles = Moles(), movement = Movement();
            SelfTestKit.WithManager(m =>
            {
                m.RequestLoadDatasetRpc(k_Moles);
                string molesId = m.ActiveLayerId;
                m.RequestAddDatasetLayerRpc(k_Movement);
                float oldSize = m.Stack.Find(molesId).PointSize;

                var history = new DashboardUndoHistory(50);
                bool Restore(PlotSnapshot s)
                {
                    m.ApplyNetworkSnapshot(s, s.TimeScrub);
                    return true;
                }
                history.Push(PlotSnapshot.Capture(m));
                m.RequestSetLayerPointSizeRpc(molesId, 0.05f);
                t.True("undo", history.TryUndo(PlotSnapshot.Capture(m), Restore));
                t.Near("undo restores the size", oldSize, m.Stack.Find(molesId).PointSize);
                t.True("redo", history.TryRedo(PlotSnapshot.Capture(m), Restore));
                t.Near("redo sets it again", 0.05f, m.Stack.Find(molesId).PointSize);
            }, (k_Moles, moles), (k_Movement, movement));
        }

        [SelfTest("PF-01: layer list rows show the size")]
        static void RowSize(SelfTestTally t)
        {
            var stack = new LayerStack();
            DatasetColumnar ds = Moles();
            PlotLayer a = stack.AddDataset(ds.Name, ds);
            a.PointSize = 0.2f * MultiplayerScatterplotManager.MaxPointSize;
            PlotLayer path = stack.AddComputed(a, ComputedLayerKind.Path);

            Model.View v = Model.Build(stack, null, null, 0);
            t.Eq("point row: stepper", true, v.Rows[0].ShowSize);
            t.Eq("point row: text", "20%", v.Rows[0].SizeText);
            t.Eq("point row: can shrink", true, v.Rows[0].CanShrink);
            t.Eq("point row: can grow", true, v.Rows[0].CanGrow);
            t.Eq("computed row: no stepper", false, v.Rows[1].ShowSize);

            a.PointSize = MultiplayerScatterplotManager.MaxPointSize;
            t.Eq("at max: cannot grow", false, Model.Build(stack, null, null, 0).Rows[0].CanGrow);

            int s0 = Model.Signature(stack, null);
            a.PointSize = 0.05f;
            t.True("signature follows the size", Model.Signature(stack, null) != s0);
        }
    }
}
