// Markdown and SillyTavern's macros for the code field (`Macros` reads them on the server): headings in teal (`type`), bold in its
// weight (`strong`), narration `*...*` italic and muted (`emphasis`), quoted speech `"..."`, `“...”` and `«...»` in the accent
// (`speech`), inline and fenced code in terracotta (`code`), list markers and roleplay tags like `<START>` in gold (`selector`), and
// macros `{{...}}` in purple (`meta`) and pale red (`regex`) — no blue token kinds.

const MacroLanguage = "macros";
const Macro = /\{\{(\s*)([#/]?[^\s}]*)([^}]*)\}\}/g;
const FenceOpen = /^( {0,3})(`{3,}|~{3,})(.*)$/;
const ThematicBreak = /^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$/;
const QuotePrefix = /^ {0,3}> ?/;
const ListPrefix = /^( {0,3})([-*+]|\d{1,9}[.)])(?=[ \t]|$)/;
const HeadingPrefix = /^( {0,3})(#{1,6})(?=[ \t]|$)/;
const SectionLine = /^( {0,3})(\[[^\]\r\n]+\])[ \t]*$/;
const InlineSpan = /\{\{[^}]*\}\}|`[^`]+`|\*\*[^*]+\*\*|__[^_]+__|~~[^~]+~~|\*(?!\s)[^*]+(?<!\s)\*|\*[^*\s]\*|"[^"\r\n]+"|“[^“”\r\n]+”|«[^«»\r\n]+»|<\/?[A-Za-z_][\w-]*>/g;

function emitMacro(line, start, match, emit) {
    const nameStart = start + 2 + match[1].length;
    const nameEnd = nameStart + match[2].length;
    const end = start + match[0].length;
    const control = /^(#if|\/if|#unless|\/unless|else)$/i.test(match[2]);

    emit(start, start + 2, "meta");

    if (nameEnd > nameStart)
        emit(nameStart, nameEnd, control ? "meta" : "regex");

    const argument = match[3].trim();

    if (argument.length > 0) {
        const argumentStart = nameEnd + match[3].indexOf(argument);

        emit(argumentStart, argumentStart + argument.length, "regex");
    }

    emit(end - 2, end, "meta");
}

function emitWithMacros(line, from, to, defaultKind, emit) {
    if (from >= to)
        return;

    const slice = line.slice(from, to);
    let cursor = 0;

    for (const match of slice.matchAll(Macro)) {
        const matchStart = match.index;
        const matchEnd = matchStart + match[0].length;

        if (defaultKind !== null && matchStart > cursor)
            emit(from + cursor, from + matchStart, defaultKind);

        emitMacro(line, from + matchStart, match, emit);
        cursor = matchEnd;
    }

    if (defaultKind !== null && cursor < slice.length)
        emit(from + cursor, to, defaultKind);
}

function tokenizeInlines(line, from, defaultKind, emit) {
    const slice = line.slice(from);
    let cursor = 0;

    for (const match of slice.matchAll(InlineSpan)) {
        const token = match[0];
        const start = from + match.index;
        const end = start + token.length;

        if (defaultKind !== null && match.index > cursor)
            emit(from + cursor, start, defaultKind);

        if (token.startsWith("{{")) {
            emitWithMacros(line, start, end, defaultKind, emit);
        } else if (token.startsWith("`")) {
            emitWithMacros(line, start, end, "code", emit);
        } else if (token.startsWith("**") || token.startsWith("__")) {
            emitWithMacros(line, start, end, "strong", emit);
        } else if (token.startsWith("~~")) {
            emitWithMacros(line, start, end, "strikethrough", emit);
        } else if (token.startsWith("*")) {
            emitWithMacros(line, start, end, "emphasis", emit);
        } else if (token.startsWith('"') || token.startsWith("“") || token.startsWith("«")) {
            emitWithMacros(line, start, end, "speech", emit);
        } else if (token.startsWith("<")) {
            emit(start, end, "selector");
        }

        cursor = match.index + token.length;
    }

    if (defaultKind !== null && cursor < slice.length)
        emit(from + cursor, line.length, defaultKind);
}

const InitialState = { fence: "" };

const tokenizer = {
    initialState: InitialState,
    tokenizeLine(line, state, emit) {
        const activeFence = typeof state?.fence === "string" ? state.fence : "";

        if (activeFence.length > 0) {
            const trimmed = line.trim();

            if (trimmed.startsWith(activeFence) && /^[`~]+$/.test(trimmed)) {
                emit(0, line.length, "code");
                return InitialState;
            }

            emitWithMacros(line, 0, line.length, "code", emit);
            return state;
        }

        const fence = FenceOpen.exec(line);

        if (fence !== null && !(fence[2][0] === "`" && fence[3].includes("`"))) {
            emitWithMacros(line, 0, line.length, "code", emit);
            return { fence: fence[2] };
        }

        if (ThematicBreak.test(line)) {
            emit(0, line.length, "comment");
            return InitialState;
        }

        const heading = HeadingPrefix.exec(line);

        if (heading !== null) {
            emitWithMacros(line, heading[1].length, line.length, "type", emit);
            return InitialState;
        }

        const section = SectionLine.exec(line);

        if (section !== null) {
            const start = section[1].length;
            emitWithMacros(line, start, start + section[2].length, "selector", emit);
            return InitialState;
        }

        const quote = QuotePrefix.exec(line);

        if (quote !== null) {
            emit(0, quote[0].length, "comment");
            tokenizeInlines(line, quote[0].length, "comment", emit);
            return InitialState;
        }

        const list = ListPrefix.exec(line);

        if (list !== null) {
            const markerStart = list[1].length;
            const markerEnd = markerStart + list[2].length;

            emit(markerStart, markerEnd, "selector");
            tokenizeInlines(line, markerEnd, null, emit);
            return InitialState;
        }

        tokenizeInlines(line, 0, null, emit);
        return InitialState;
    }
};

// The names `PromptBuilder` and `Macros` know, offered as the reader types, each in the colour the field draws it in.
const completions = [
    "char", "user", "system", "original", "description", "personality", "scenario", "persona", "wiBefore", "wiAfter",
    "loreBefore", "loreAfter", "mesExamples", "mesExamplesRaw", "newline", "noop", "trim"
].map(label => ({ label, kind: "regex" })).concat(["if", "else"].map(label => ({ label, kind: "meta" })));

if (typeof document !== "undefined" && !document.getElementById("chatai-macros-style")) {
    const style = document.createElement("style");
    style.id = "chatai-macros-style";
    style.textContent =
        '.ui-code-input[data-ui-code-language="macros"] .ui-tk-emphasis { color: var(--ui-text-muted); }\n' +
        '.ui-code-input[data-ui-code-language="macros"] .ui-tk-speech { color: var(--ui-color-accent-ink); }';
    document.head.appendChild(style);
}

// The code input's script may load after this one; until it does, a language waits in the queue it drains on load.
const api = (window.NEStandardUICodeInput ??= {});

if (typeof api.registerLanguage === "function")
    api.registerLanguage(MacroLanguage, tokenizer, completions);
else
    (api.__pendingLanguages ??= []).push({ id: MacroLanguage, tokenizer, completions });
