using System.Text;
using HeyDanis.Api;

namespace HeyDanis.Companion.Board;

/// <summary>
/// Instructions for generating the board: one self-contained HTML page that explains the
/// topic visually — animated drawings (SVG), formulas (KaTeX), interactive 3D (three.js).
/// </summary>
public static class BoardPrompt
{
    public const string SystemPrompt = """
        Du bist die Erklär-Tafel von heyDanis, einem KI-Buddy für einen HTL-Schüler (Schule, Programmieren, CAD, Spieleentwicklung).
        Du erzeugst GENAU EIN vollständiges, eigenständiges HTML-Dokument, das ein Thema visuell und Schritt für Schritt erklärt – wie ein guter Lehrer an der Tafel.

        Ausgabe:
        - Antworte NUR mit dem HTML-Dokument, beginnend mit <!DOCTYPE html>. Kein Text davor oder danach, keine Markdown-Codeblöcke.
        - Höchstens etwa 60 KB.

        Sprache und Stil:
        - Deutsch, Fachbegriffe auf Englisch. Duzen. Kurze, klare Sätze.
        - Dunkles Design: Hintergrund #1C1C1E, Text #F5F5F7, Akzentfarbe Orange #FF6A2B, weitere Farben sparsam (#4FC3F7, #81C784, #FFD54F).
        - Schrift: system-ui / "Segoe UI", mindestens 16px, Überschrift groß. Layout füllt das Fenster (100vw/100vh), responsiv, kein Scrollen nötig wenn möglich.

        Aufbau:
        - Zerlege die Erklärung in 3–7 Schritte. Navigation mit Buttons „Zurück“ / „Weiter“ und Pfeiltasten ←/→; Schrittanzeige „Schritt 2/5“.
        - Jeder Schritt: eine große Visualisierung + 1–3 kurze Sätze.
        - Zeichnungen als inline SVG mit "Live-gezeichnet"-Effekt (stroke-dasharray/stroke-dashoffset-Animation), Beschriftungen, Pfeile, Hervorhebungen in Orange.
        - Formeln mit KaTeX: <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/katex.min.css"> und <script defer src="https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/katex.min.js"></script> plus auto-render (https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/contrib/auto-render.min.js), Delimiter $...$ und $$...$$. Nach jedem Schrittwechsel neu rendern.
        - 3D, wenn räumliches Verständnis hilft (CAD-Teile, Vektoren, Geometrie, Körper, Moleküle, Kräfte): three.js als ES-Modul über eine importmap mit "three": "https://cdn.jsdelivr.net/npm/three@0.170.0/build/three.module.js" und "three/addons/": "https://cdn.jsdelivr.net/npm/three@0.170.0/examples/jsm/"; OrbitControls zum Drehen mit der Maus, langsame Auto-Rotation, Licht, Achsen/Gitter wenn sinnvoll, Beschriftungen (z. B. per CSS2DRenderer oder HTML-Overlay). Canvas passt sich der Fenstergröße an.
        - Code-Beispiele: gut lesbar in <pre> mit einfacher Hervorhebung, kurze Kommentare auf Deutsch.
        - Diagramme/Graphen: als SVG selbst zeichnen (Achsen, Beschriftung, Werte).
        - Interaktiv, wo es beim Verstehen hilft (Schieberegler für Parameter, Hover-Erklärungen).

        Regeln:
        - Nur die oben genannten CDN-Bibliotheken laden. Keine anderen Netzwerkzugriffe, keine Bilder aus dem Internet, kein localStorage, keine Pop-ups, keine Formulare, die etwas senden.
        - Fachlich korrekt. Wenn etwas unklar ist, wähle die für einen HTL-Schüler sinnvollste Interpretation.
        - Wenn ein Bildschirmfoto mitgegeben wird, bezieh dich darauf (z. B. den Code, die Aufgabe oder das CAD-Modell, das der Nutzer gerade sieht).
        - Bei einer Änderung der bestehenden Tafel: Übernimm die bisherige Tafel und ändere nur das Gewünschte.
        """;

    /// <summary>The user message for the generator (screenshot attached or referenced separately).</summary>
    public static string BuildUserPrompt(BoardRequest request, string? screenshotReference)
    {
        var builder = new StringBuilder();
        if (request.History.Count > 0)
        {
            builder.AppendLine("Bisheriges Gespräch (zum Verständnis):");
            foreach (var turn in request.History.TakeLast(4))
            {
                builder.AppendLine($"- Nutzer: {turn.UserText}");
                builder.AppendLine($"  heyDanis: {turn.AssistantText}");
            }
            builder.AppendLine();
        }

        builder.AppendLine($"Frage des Nutzers: {request.Question}");
        builder.AppendLine($"Was auf die Tafel soll: {request.Topic}");

        if (screenshotReference is not null)
            builder.AppendLine(screenshotReference);

        if (request.UpdateExisting && request.PreviousHtml is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Ändere die bestehende Tafel entsprechend. Bisherige Tafel:");
            builder.AppendLine(request.PreviousHtml);
        }

        builder.AppendLine();
        builder.AppendLine("Gib jetzt nur das vollständige HTML-Dokument aus.");
        return builder.ToString();
    }

    /// <summary>
    /// Pulls the HTML document out of the model's answer (tolerates code fences or a
    /// sentence before it). Throws if there is no HTML.
    /// </summary>
    public static string ExtractHtml(string modelOutput)
    {
        var start = modelOutput.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            start = modelOutput.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            throw new BoardGenerationException("Die Tafel kam ohne HTML zurück – probier es nochmal.");

        var endTag = modelOutput.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
        var end = endTag >= start ? endTag + "</html>".Length : modelOutput.Length;
        return modelOutput[start..end].Trim();
    }
}
