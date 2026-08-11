namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>
/// The same fifteen cases, written in each language a game may be written in.
///
/// One set was not enough, and the reason is structural rather than tidy. With English fixtures
/// alone, anyone translating INTO English was asked to translate English into English — a job the
/// mod never gives a model, and one that goes badly in a way that says nothing: measured, a
/// translation-specialised model answered in Portuguese and then in Russian, and a small model
/// dropped markers it holds perfectly when there is real work to do. Translating into English is
/// one of the most common uses of the mod, so that was not a corner case.
///
/// The choice of languages is about what each one BREAKS, not about which is worth supporting —
/// every target language is supported and always was:
///   English   the baseline: spaced Latin, and what most games are written in
///   Spanish   a second Latin source, so an English target still gets a real translation.
///             Inverted punctuation takes the "invents no punctuation" check from the other end
///   Chinese   no spaces at all: trips the mod's own single-word rule, and leaves markers glued
///             to the text with no whitespace to lean on. Also the first real source of
///             untranslated games
///   Japanese  mixed scripts and full-width punctuation, which models silently convert to ASCII
///   Arabic    right to left: the markers are left-to-right islands inside it, so their order in
///             the string and their order on screen are not the same thing
///   Russian   Cyrillic, spaced — little new structurally, but a very common source in practice
///   Korean    the particle trap: 은/는 and 이/가 are chosen by the last sound of the word before
///             them, so a name placeholder makes every choice wrong. Nothing else tests [!STR*0]
///             this hard
///
/// ⚠ The short strings were written by the project, not by native speakers of every one of these
/// languages. They are deliberately the plainest phrases a game holds — "Loading", "Save" — and
/// every check on them is structural, so an awkward turn of phrase costs a reader nothing and
/// changes no verdict. A native correction is still welcome for any of them.
/// </summary>
public sealed class Fixtures
{
    /// <summary>The language name as the mod writes it in its prompt.</summary>
    public required string Language { get; init; }

    /// <summary>Two-letter code, to compare against the target without matching on names.</summary>
    public required string Code { get; init; }

    /// <summary>
    /// False for scripts with no upper and lower case. The shouted-label case is skipped for them
    /// rather than failed: asking Chinese to answer in capitals is asking for something that does
    /// not exist, and a KO there would say nothing about the model.
    /// </summary>
    public bool HasCase { get; init; } = true;

    /// <summary>
    /// The characters this language ends a sentence with. The "invents no punctuation" check tests
    /// for these, not for a full stop: Japanese and Chinese end with 。 and a model that adds one
    /// has done exactly what the check is looking for.
    /// </summary>
    public required string[] SentenceEnders { get; init; }

    public required string PlainLine { get; init; }
    public required string NoPunctuation { get; init; }
    public required string SingleWord { get; init; }

    /// <summary>Null where the script has no case.</summary>
    public string? ShoutedLabel { get; init; }

    public required string TechnicalTerms { get; init; }
    public required string Shortcut { get; init; }
    public required string OnePlaceholder { get; init; }
    public required string TwoPlaceholders { get; init; }
    public required string NameInjected { get; init; }
    public required string BlankLine { get; init; }
    public required string TrailingBreak { get; init; }
    public required string MarkupSpan { get; init; }
    public required string Paragraph { get; init; }
    public required string MarkersInRow { get; init; }

    /// <summary>
    /// Nothing but markers, and the same in every language because there is nothing to write: a
    /// number inside its own colour tag, the shape behind every coloured counter in a HUD.
    /// </summary>
    public const string MarkersOnly = "[!t*0][!v*0][!t*1]";

    /// <summary>
    /// A constructed language, for the last case of all.
    ///
    /// It can be neither a source nor a target here, which is the point: the existing "refuses the
    /// wrong language" case uses a real language, and a real language can be the very one the
    /// reader is translating into — as it was, silently, for every French user until now.
    ///
    /// It is not a joke case either. Games carry invented languages, proper nouns and outright
    /// gibberish, and a model that confidently "translates" them is the one that will quietly
    /// rewrite them in a real game.
    /// </summary>
    public const string Klingon = "nuqneH, tlhIngan maH!";

    public static readonly IReadOnlyList<Fixtures> All = new[]
    {
        new Fixtures
        {
            Language = "English",
            Code = "en",
            SentenceEnders = new[] { ".", "!" },
            PlainLine = "Start Game",
            NoPunctuation = "Loading",
            SingleWord = "Save",
            ShoutedLabel = "NEW GAME",
            TechnicalTerms = "Your API key is stored in JSON",
            Shortcut = "Press Ctrl+F10 to open settings",
            OnePlaceholder = "Press [!v*0] to continue",
            TwoPlaceholders = "Press [!v*0] to save[!nl]Your API key is required",
            NameInjected = "[!STR*0] has joined the crew",
            BlankLine = "Objective complete[!nl][!nl]Return to the ship",
            TrailingBreak = "Settings saved[!nl]",
            MarkupSpan = "[!t*0]Warning[!t*1]: shields at [!v*0] percent",
            MarkersInRow = "[!t*0]Loaded[!t*1] [!v*0]/[!v*1]/[!v*2]/[!v*3]/[!v*4]/[!v*5]",
            Paragraph =
                "[!t*0]Warning[!t*1][!nl]The reactor is running at [!v*0] percent of its rated "
                + "output. Vent the coolant before the next jump, or the crew will not survive it. "
                + "Repairs cost [!v*1] credits and take [!v*2] cycles, and nothing else can be "
                + "built while they are under way.",
        },

        new Fixtures
        {
            Language = "Spanish",
            Code = "es",
            // The inverted marks matter here: a model that opens one the source never had has
            // invented punctuation just as surely as one that adds a full stop.
            SentenceEnders = new[] { ".", "!", "¡", "?", "¿" },
            PlainLine = "Iniciar partida",
            NoPunctuation = "Cargando",
            SingleWord = "Guardar",
            ShoutedLabel = "NUEVA PARTIDA",
            TechnicalTerms = "Tu clave API se guarda en JSON",
            Shortcut = "Pulsa Ctrl+F10 para abrir los ajustes",
            OnePlaceholder = "Pulsa [!v*0] para continuar",
            TwoPlaceholders = "Pulsa [!v*0] para guardar[!nl]Se requiere tu clave API",
            NameInjected = "[!STR*0] se ha unido a la tripulación",
            BlankLine = "Objetivo completado[!nl][!nl]Vuelve a la nave",
            TrailingBreak = "Ajustes guardados[!nl]",
            MarkupSpan = "[!t*0]Aviso[!t*1]: escudos al [!v*0] por ciento",
            MarkersInRow = "[!t*0]Cargado[!t*1] [!v*0]/[!v*1]/[!v*2]/[!v*3]/[!v*4]/[!v*5]",
            Paragraph =
                "[!t*0]Aviso[!t*1][!nl]El reactor funciona al [!v*0] por ciento de su potencia "
                + "nominal. Purga el refrigerante antes del próximo salto o la tripulación no "
                + "sobrevivirá. Las reparaciones cuestan [!v*1] créditos y tardan [!v*2] ciclos, y "
                + "no se puede construir nada más mientras duran.",
        },

        new Fixtures
        {
            Language = "Russian",
            Code = "ru",
            SentenceEnders = new[] { ".", "!" },
            PlainLine = "Начать игру",
            NoPunctuation = "Загрузка",
            SingleWord = "Сохранить",
            ShoutedLabel = "НОВАЯ ИГРА",
            TechnicalTerms = "Ваш ключ API хранится в JSON",
            Shortcut = "Нажмите Ctrl+F10, чтобы открыть настройки",
            OnePlaceholder = "Нажмите [!v*0], чтобы продолжить",
            TwoPlaceholders = "Нажмите [!v*0], чтобы сохранить[!nl]Требуется ключ API",
            NameInjected = "[!STR*0] присоединился к экипажу",
            BlankLine = "Задание выполнено[!nl][!nl]Вернитесь на корабль",
            TrailingBreak = "Настройки сохранены[!nl]",
            MarkupSpan = "[!t*0]Внимание[!t*1]: щиты на [!v*0] процентов",
            MarkersInRow = "[!t*0]Загружено[!t*1] [!v*0]/[!v*1]/[!v*2]/[!v*3]/[!v*4]/[!v*5]",
            Paragraph =
                "[!t*0]Внимание[!t*1][!nl]Реактор работает на [!v*0] процентов от номинальной "
                + "мощности. Стравите охладитель до следующего прыжка, иначе экипаж не выживет. "
                + "Ремонт стоит [!v*1] кредитов и занимает [!v*2] циклов, и пока он идёт, ничего "
                + "другого построить нельзя.",
        },

        new Fixtures
        {
            Language = "Chinese",
            Code = "zh",
            HasCase = false,
            SentenceEnders = new[] { "。", "！", ".", "!" },
            PlainLine = "开始游戏",
            NoPunctuation = "加载中",
            SingleWord = "保存",
            TechnicalTerms = "你的 API 密钥保存在 JSON 中",
            Shortcut = "按 Ctrl+F10 打开设置",
            OnePlaceholder = "按 [!v*0] 继续",
            TwoPlaceholders = "按 [!v*0] 保存[!nl]需要你的 API 密钥",
            NameInjected = "[!STR*0] 加入了队伍",
            BlankLine = "目标完成[!nl][!nl]返回飞船",
            TrailingBreak = "设置已保存[!nl]",
            MarkupSpan = "[!t*0]警告[!t*1]：护盾剩余 [!v*0] %",
            MarkersInRow = "[!t*0]已装载[!t*1] [!v*0]/[!v*1]/[!v*2]/[!v*3]/[!v*4]/[!v*5]",
            Paragraph =
                "[!t*0]警告[!t*1][!nl]反应堆正以额定功率的 [!v*0] % 运行。下一次跃迁前请排出冷却剂，"
                + "否则船员无法生还。维修需要 [!v*1] 信用点和 [!v*2] 个周期，期间无法建造其他任何东西。",
        },

        new Fixtures
        {
            Language = "Japanese",
            Code = "ja",
            HasCase = false,
            // Full-width punctuation is the trap: models convert 。 to a plain full stop without
            // being asked, which is a change to text the mod will print verbatim.
            SentenceEnders = new[] { "。", "！", ".", "!" },
            PlainLine = "ゲームを開始",
            NoPunctuation = "読み込み中",
            SingleWord = "保存",
            TechnicalTerms = "API キーは JSON に保存されます",
            Shortcut = "Ctrl+F10 を押して設定を開く",
            OnePlaceholder = "[!v*0] を押して続ける",
            TwoPlaceholders = "[!v*0] を押して保存[!nl]API キーが必要です",
            NameInjected = "[!STR*0] が乗組員に加わった",
            BlankLine = "目標達成[!nl][!nl]船に戻れ",
            TrailingBreak = "設定を保存しました[!nl]",
            MarkupSpan = "[!t*0]警告[!t*1]：シールド残り [!v*0] パーセント",
            MarkersInRow = "[!t*0]装填済み[!t*1] [!v*0]/[!v*1]/[!v*2]/[!v*3]/[!v*4]/[!v*5]",
            Paragraph =
                "[!t*0]警告[!t*1][!nl]リアクターは定格出力の [!v*0] パーセントで稼働中です。"
                + "次のジャンプの前に冷却材を排出してください。さもなければ乗組員は助かりません。"
                + "修理には [!v*1] クレジットと [!v*2] サイクルが必要で、その間は他に何も建造できません。",
        },

        new Fixtures
        {
            Language = "Korean",
            Code = "ko",
            HasCase = false,
            SentenceEnders = new[] { ".", "!" },
            PlainLine = "게임 시작",
            NoPunctuation = "불러오는 중",
            SingleWord = "저장",
            TechnicalTerms = "API 키는 JSON에 저장됩니다",
            Shortcut = "Ctrl+F10을 눌러 설정을 엽니다",
            OnePlaceholder = "[!v*0]을 눌러 계속하기",
            TwoPlaceholders = "[!v*0]을 눌러 저장[!nl]API 키가 필요합니다",
            // The particle trap: 은/는 and 이/가 depend on the final sound of the word before them,
            // which a placeholder hides. No choice is right, and that is the test.
            NameInjected = "[!STR*0]이(가) 승무원으로 합류했습니다",
            BlankLine = "목표 완료[!nl][!nl]함선으로 복귀하십시오",
            TrailingBreak = "설정이 저장되었습니다[!nl]",
            MarkupSpan = "[!t*0]경고[!t*1]: 방어막 [!v*0] 퍼센트",
            MarkersInRow = "[!t*0]장전됨[!t*1] [!v*0]/[!v*1]/[!v*2]/[!v*3]/[!v*4]/[!v*5]",
            Paragraph =
                "[!t*0]경고[!t*1][!nl]원자로가 정격 출력의 [!v*0] 퍼센트로 작동 중입니다. "
                + "다음 도약 전에 냉각수를 배출하십시오. 그렇지 않으면 승무원은 살아남지 못합니다. "
                + "수리에는 [!v*1] 크레딧과 [!v*2] 주기가 필요하며, 그동안 다른 것은 건조할 수 없습니다.",
        },

        new Fixtures
        {
            Language = "Arabic",
            Code = "ar",
            HasCase = false,
            SentenceEnders = new[] { ".", "!", "؟" },
            PlainLine = "ابدأ اللعبة",
            NoPunctuation = "جارٍ التحميل",
            SingleWord = "حفظ",
            TechnicalTerms = "مفتاح API محفوظ في JSON",
            Shortcut = "اضغط Ctrl+F10 لفتح الإعدادات",
            OnePlaceholder = "اضغط [!v*0] للمتابعة",
            TwoPlaceholders = "اضغط [!v*0] للحفظ[!nl]مفتاح API مطلوب",
            NameInjected = "انضم [!STR*0] إلى الطاقم",
            BlankLine = "اكتمل الهدف[!nl][!nl]عد إلى السفينة",
            TrailingBreak = "تم حفظ الإعدادات[!nl]",
            MarkupSpan = "[!t*0]تحذير[!t*1]: الدروع عند [!v*0] بالمئة",
            MarkersInRow = "[!t*0]تم التحميل[!t*1] [!v*0]/[!v*1]/[!v*2]/[!v*3]/[!v*4]/[!v*5]",
            Paragraph =
                "[!t*0]تحذير[!t*1][!nl]يعمل المفاعل عند [!v*0] بالمئة من طاقته المقررة. "
                + "أفرغ سائل التبريد قبل القفزة التالية وإلا فلن ينجو الطاقم. "
                + "تكلف الإصلاحات [!v*1] رصيدًا وتستغرق [!v*2] دورات، ولا يمكن بناء أي شيء آخر خلالها.",
        },
    };

    /// <summary>
    /// The set to translate FROM, given what the reader translates INTO.
    ///
    /// English unless the target is English, and then Spanish — deliberately another Latin
    /// language rather than the most exotic one available. Testing Arabic to English measures a
    /// harder job than the mod usually does, and would mark models down for the wrong reason.
    /// Whoever plays games written in Chinese can ask for Chinese; that is their real case, and
    /// only they know it.
    /// </summary>
    public static Fixtures For(string targetCode)
    {
        var target = targetCode.Trim().ToLowerInvariant();

        var english = All[0];
        if (!string.Equals(english.Code, target, StringComparison.OrdinalIgnoreCase)) return english;

        return All.First(set => !string.Equals(set.Code, target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The set someone asked for by code, or null when we hold nothing in it.</summary>
    public static Fixtures? ByCode(string? code) =>
        code is null
            ? null
            : All.FirstOrDefault(set => string.Equals(set.Code, code.Trim(),
                                                      StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A sentence in a language that is neither the source nor the target, for the case that asks
    /// a model to refuse what it must not translate.
    ///
    /// Chosen rather than fixed, because a fixed one is eventually somebody's target language: the
    /// French sentence that lived here asked every French player's model to refuse French while
    /// translating into French, and it had done so quietly since the day it was written.
    /// </summary>
    public static Fixtures ForeignTo(Fixtures source, string targetCode) =>
        All.First(set => set.Code != source.Code
                         && !string.Equals(set.Code, targetCode, StringComparison.OrdinalIgnoreCase));
}
