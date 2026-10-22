using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lazo;

/// <summary>El panel de chat con grupos y mensajes eliminados, dibujado fuera de pantalla (no abre ninguna ventana).</summary>
internal static class ChatPanelSmoke
{
    private static bool _ok = true;
    private static void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok  " : "  FALLA ") + what); if (!ok) _ok = false; }

    [STAThread]
    private static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "chat-panel";
        Directory.CreateDirectory(output);
        string root = Path.Combine(Path.GetTempPath(), "lazo-chatui-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(root);
        ChatStore.Root = root;
        try
        {
            Theme.Load();
            Guid ana = Guid.NewGuid(), luis = Guid.NewGuid(), me = Guid.NewGuid();
            ChatStore.Append(ana, "Ana", false, "Hola, ¿ya revisaste el modelo?", "aaaa0001");
            ChatStore.Append(ana, "Ana", true, "Sí, lo reviso hoy", "aaaa0002");
            ChatStore.Append(ana, "Ana", false, "borrado", "aaaa0003");
            ChatStore.MarkDeleted(ana, "aaaa0003");
            ChatStore.Append(ana, "Ana", true, "este lo borré yo", "aaaa0004");
            ChatStore.MarkDeleted(ana, "aaaa0004", null, false);
            ChatGroup group = new ChatGroup { Id = Guid.NewGuid(), Name = "Obra torre A", Owner = me };
            group.Members.Add(new GroupMember { Id = ana, Name = "Ana" });
            group.Members.Add(new GroupMember { Id = luis, Name = "Luis" });
            ChatGroups.Save(group);
            ChatStore.EnsureThread(group.Id, group.Name);
            ChatStore.AppendIncoming(group.Id, group.Name, "Subí los planos a la carpeta", "bbbb0001", "Luis");
            ChatStore.Append(group.Id, group.Name, true, "Perfecto, gracias", "bbbb0002", "Jhon");
            ChatStore.AppendIncoming(group.Id, group.Name, "Revisen el nivel 3 por favor", "bbbb0003", "Ana");

            foreach (ThemeKind kind in new[] { ThemeKind.Dark, ThemeKind.Raycast, ThemeKind.Wine })
            {
                Theme.SetForPreview(kind);
                ChatPanel panel = new ChatPanel(null, () => { }, () => { }, () => { });
                typeof(ChatPanel).GetField("_peers", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(panel, new List<Peer> { new Peer { Id = ana, Name = "Ana" } });
                foreach (Guid thread in new[] { ana, group.Id })
                {
                    string name = thread == ana ? "Ana" : group.Name;
                    try
                    {
                        panel.Open(thread, name);
                        panel.Measure(new Size(780, 520));
                        panel.Arrange(new Rect(0, 0, 780, 520));
                        panel.UpdateLayout();
                        RenderTargetBitmap bitmap = new RenderTargetBitmap(780, 520, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(panel);
                        PngBitmapEncoder encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (FileStream file = File.Create(Path.Combine(output, kind + "-" + (thread == ana ? "directo" : "grupo") + ".png"))) encoder.Save(file);
                        Check(true, kind + "/" + name + ": se dibuja sin errores");
                    }
                    catch (Exception ex) { Check(false, kind + "/" + name + ": " + ex.GetType().Name + ": " + ex.Message); }
                }
                // Formulario de nuevo grupo
                try
                {
                    typeof(ChatPanel).GetMethod("ShowNewGroup", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(panel, null);
                    panel.Measure(new Size(780, 520));
                    panel.Arrange(new Rect(0, 0, 780, 520));
                    panel.UpdateLayout();
                    RenderTargetBitmap bitmap = new RenderTargetBitmap(780, 520, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(panel);
                    PngBitmapEncoder encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (FileStream file = File.Create(Path.Combine(output, kind + "-nuevo-grupo.png"))) encoder.Save(file);
                    Check(true, kind + ": el formulario de nuevo grupo se dibuja");
                }
                catch (Exception ex) { Check(false, kind + ": formulario " + ex.GetType().Name + ": " + ex.Message); }
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        Console.WriteLine(_ok ? "OK" : "FALLÓ");
        return _ok ? 0 : 1;
    }
}
