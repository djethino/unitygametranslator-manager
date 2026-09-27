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
    /// A name, then a title in its own colour — "Wudang Sect" and a coloured "Elder" — the shape of
    /// every faction rank in a character sheet.
    ///
    /// 🔴 **Added 2026-09-26 from a real game**, where ranks like this stayed untranslated on every
    /// screen: translating often moves the title in front of the name, and a model that is not
    /// told what a tag pair IS keeps the pair where it stood, or drops it, or colours the name.
    /// Checked on structure alone — a pair around words, the name outside it — never on wording.
    /// </summary>
    public required string ColouredTitle { get; init; }

    /// <summary>The name in <see cref="ColouredTitle"/>, as the source and a Latin answer write it: what must stay out of the colour.</summary>
    public required string[] TitleName { get; init; }

    /// <summary>
    /// A label of the game's own in square brackets, inside a colour, in a two-line tooltip — the
    /// shape "Critical:" then a coloured "[Attack]" then its effect.
    ///
    /// 🔴 **Added 2026-09-26 from a real game**, where models turned the bracketed label into prose
    /// ("[Attack]" became "d'attaque") and every such tooltip stayed untranslated.
    /// </summary>
    public required string ColouredLabel { get; init; }

    /// <summary>
    /// A speaker's name and a bracketed word of the game's own, together inside one colour, then
    /// the line — "Thomas[Thought]: " in a dialogue history.
    ///
    /// 🔴 **Added 2026-09-27 from a real game**, where every such line came back with the bracketed
    /// word untranslated: sent between two tag markers, "[Thought]" has the shape of the markers a
    /// model is told to keep as they are.
    /// </summary>
    public required string SpeakerLabel { get; init; }

    /// <summary>The bracketed word of <see cref="SpeakerLabel"/> as the source writes it: what must not come back.</summary>
    public required string SpeakerLabelWord { get; init; }

    /// <summary>
    /// Everything at once: tags, inserted text, four numbers, three line breaks and a blank one.
    ///
    /// ⚠ **In addition to <see cref="Paragraph"/>, never instead of it.** That one isolates
    /// DISTANCE — a marker is lost in the middle of a long line, not on a short one — and this one
    /// measures the pile-up. Merged into a single case, a failure would not say which of the two
    /// went wrong, and the pile-up is the harder question of the two to act on.
    ///
    /// ⚠ It is the only long text carrying [!STR*0], the marker that was tested on three words
    /// until now and is the most fragile of the four: it holds text that must come through
    /// untranslated, and in Korean it hides the sound the following particle is chosen by.
    /// </summary>
    public required string ParagraphFull { get; init; }

    /// <summary>
    /// A line whose register is the whole content — wry, spoken, a shrug.
    ///
    /// 🔴 Nothing here can be checked by machine, and that is the point: it exists to be READ. The
    /// prompt asks for the tone of the source to be kept, and no structural test can tell whether
    /// it was. A translation that turns this into a flat statement of fact obeys every rule the
    /// bench can verify and has still lost what the line was.
    /// </summary>
    public required string ToneMarked { get; init; }

    /// <summary>
    /// Nothing but markers, and the same in every language because there is nothing to write: a
    /// number inside its own colour tag, the shape behind every coloured counter in a HUD.
    /// </summary>
    public const string MarkersOnly = "<color=#FFCC00>[!v*0]</color>";

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

    /// <summary>
    /// A game that does not exist, and its description.
    ///
    /// 🔴 **Invented rather than borrowed, and that settles two things at once.** Nothing is taken
    /// from anybody's game — neither the name nor the words — and we can be CERTAIN the model has
    /// never heard of it. With a real title we would never know whether it knew the game, so we
    /// would not know what we were measuring.
    ///
    /// What it makes visible, in two separate questions:
    ///   - does the model INVENT? Lore that is in neither the source nor the description is
    ///     something it made up, and it will do the same inside a real game;
    ///   - does the context STEER the wording, which is the effect we are after rather than a
    ///     fault — that is why it is asked in two steps, name alone and then name plus
    ///     description.
    ///
    /// ⚠ **Coherent with the fixtures on purpose, never a trap.** A medieval description over
    /// spacecraft text would prove nothing anyone needs: whoever writes an incoherent description
    /// in the mod gets an incoherent result, and that is theirs to fix. What is worth measuring is
    /// what an honest description does.
    ///
    /// ⚠ Checked against published games before being fixed here (2026-08-18, nothing found). If a
    /// game ever takes this name, the point of the case is gone and it needs another one.
    /// </summary>
    public const string InventedGame = "Halyard Nine";

    /// <summary>The description its author might have written. See <see cref="InventedGame"/>.</summary>
    public const string InventedGameContext = "a science fiction game about a salvage ship and its crew";

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
            TwoPlaceholders = "Press [!v*0] to save\nYour API key is required",
            NameInjected = "[!STR*0] has joined the crew",
            BlankLine = "Objective complete\n\nReturn to the ship",
            TrailingBreak = "Settings saved\n",
            MarkupSpan = "<color=#FFCC00>Warning</color>: shields at [!v*0] percent",
            MarkersInRow = "<color=#FFCC00>Loaded</color> [!v*0]/[!v*1]/[!v*2]/[!v*3]/[!v*4]/[!v*5]",
            ColouredTitle = "Wudang Sect <color=#FFCC00>Elder</color>",
            TitleName = new[] { "Wudang" },
            ColouredLabel = "Critical:\n<color=#FFA500>[Attack]</color>Damage doubled",
            SpeakerLabel = "<color=#f3e584>Thomas[Thought]: </color>The chemicals are so strong. I'm choking here.",
            SpeakerLabelWord = "[Thought]",
            Paragraph =
                "<color=#FFCC00>Warning</color>\nThe reactor is running at [!v*0] percent of its rated "
                + "output. Vent the coolant before the next jump, or the crew will not survive it. "
                + "Repairs cost [!v*1] credits and take [!v*2] cycles, and nothing else can be "
                + "built while they are under way.",
            ParagraphFull =
                "<color=#FFCC00>Salvage report</color>\n[!STR*0] was recovered from the wreck in bay "
                + "[!v*0].\n\nRepairs cost [!v*1] credits and take [!v*2] cycles. The crew "
                + "cannot work while the reactor stays below [!v*3] percent.",
            ToneMarked = "Well, that went about as well as anyone expected.",
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
            TwoPlaceholders = "Pulsa [!v*0] para guardar\nSe requiere tu clave API",
            NameInjected = "[!STR*0] se ha unido a la tripulación",
            BlankLine = "Objetivo completado\n\nVuelve a la nave",
            TrailingBreak = "Ajustes guardados\n",
            MarkupSpan = "<color=#FFCC00>Aviso</color>: escudos al [!v*0] por ciento",
            MarkersInRow = "<color=#FFCC00>Cargado</color> [!v*0]/[!v*1]/[!v*2]/[!v*3]/[!v*4]/[!v*5]",
            ColouredTitle = "Secta Wudang <color=#FFCC00>Anciano</color>",
            TitleName = new[] { "Wudang" },
            ColouredLabel = "Crítico:\n<color=#FFA500>[Ataque]</color>Daño doble",
            SpeakerLabel = "<color=#f3e584>Tomás[Pensamiento]: </color>Los productos químicos son muy fuertes. Me estoy ahogando aquí.",
            SpeakerLabelWord = "[Pensamiento]",
            Paragraph =
                "<color=#FFCC00>Aviso</color>\nEl reactor funciona al [!v*0] por ciento de su potencia "
                + "nominal. Purga el refrigerante antes del próximo salto o la tripulación no "
                + "sobrevivirá. Las reparaciones cuestan [!v*1] créditos y tardan [!v*2] ciclos, y "
                + "no se puede construir nada más mientras duran.",
            ParagraphFull =
                "<color=#FFCC00>Informe de rescate</color>\n[!STR*0] se recuperó de los restos en la "
                + "bahía [!v*0].\n\nLas reparaciones cuestan [!v*1] créditos y tardan "
                + "[!v*2] ciclos. La tripulación no puede trabajar mientras el reactor siga por "
                + "debajo del [!v*3] por ciento.",
            ToneMarked = "Bueno, salió justo como todos esperaban.",
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
            TwoPlaceholders = "Нажмите [!v*0], чтобы сохранить\nТребуется ключ API",
            NameInjected = "[!STR*0] присоединился к экипажу",
            BlankLine = "Задание выполнено\n\nВернитесь на корабль",
            TrailingBreak = "Настройки сохранены\n",
            MarkupSpan = "<color=#FFCC00>Внимание</color>: щиты на [!v*0] процентов",
            MarkersInRow = "<color=#FFCC00>Загружено</color> [!v*0]/[!v*1]/[!v*2]/[!v*3]/[!v*4]/[!v*5]",
            ColouredTitle = "Секта Удан <color=#FFCC00>Старейшина</color>",
            TitleName = new[] { "Удан", "Wudang" },
            ColouredLabel = "Крит:\n<color=#FFA500>[Атака]</color>Двойной урон",
            SpeakerLabel = "<color=#f3e584>Томас[Мысли]: </color>Химикаты такие едкие. Я здесь задыхаюсь.",
            SpeakerLabelWord = "[Мысли]",
            Paragraph =
                "<color=#FFCC00>Внимание</color>\nРеактор работает на [!v*0] процентов от номинальной "
                + "мощности. Стравите охладитель до следующего прыжка, иначе экипаж не выживет. "
                + "Ремонт стоит [!v*1] кредитов и занимает [!v*2] циклов, и пока он идёт, ничего "
                + "другого построить нельзя.",
            ParagraphFull =
                "<color=#FFCC00>Отчёт о спасении</color>\n[!STR*0] извлечён из обломков в отсеке "
                + "[!v*0].\n\nРемонт стоит [!v*1] кредитов и занимает [!v*2] циклов. "
                + "Экипаж не может работать, пока реактор остаётся ниже [!v*3] процентов.",
            ToneMarked = "Что ж, всё прошло именно так, как все и ожидали.",
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
            TwoPlaceholders = "按 [!v*0] 保存\n需要你的 API 密钥",
            NameInjected = "[!STR*0] 加入了队伍",
            BlankLine = "目标完成\n\n返回飞船",
            TrailingBreak = "设置已保存\n",
            MarkupSpan = "<color=#FFCC00>警告</color>：护盾剩余 [!v*0] %",
            MarkersInRow = "<color=#FFCC00>已装载</color> [!v*0]/[!v*1]/[!v*2]/[!v*3]/[!v*4]/[!v*5]",
            ColouredTitle = "武当派<color=#FFCC00>掌门</color>",
            TitleName = new[] { "武当", "Wudang" },
            ColouredLabel = "暴击:\n<color=#FFA500>[攻]</color>伤害加倍",
            SpeakerLabel = "<color=#f3e584>托马斯[内心]: </color>化学品的气味太冲了，我快要窒息了。",
            SpeakerLabelWord = "[内心]",
            Paragraph =
                "<color=#FFCC00>警告</color>\n反应堆正以额定功率的 [!v*0] % 运行。下一次跃迁前请排出冷却剂，"
                + "否则船员无法生还。维修需要 [!v*1] 信用点和 [!v*2] 个周期，期间无法建造其他任何东西。",
            ParagraphFull =
                "<color=#FFCC00>打捞报告</color>\n[!STR*0] 已从 [!v*0] 号舱的残骸中回收。\n\n"
                + "维修需要 [!v*1] 信用点和 [!v*2] 个周期。反应堆低于 [!v*3] % 时，船员无法工作。",
            ToneMarked = "好吧，结果和大家想的一样。",
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
            TwoPlaceholders = "[!v*0] を押して保存\nAPI キーが必要です",
            NameInjected = "[!STR*0] が乗組員に加わった",
            BlankLine = "目標達成\n\n船に戻れ",
            TrailingBreak = "設定を保存しました\n",
            MarkupSpan = "<color=#FFCC00>警告</color>：シールド残り [!v*0] パーセント",
            MarkersInRow = "<color=#FFCC00>装填済み</color> [!v*0]/[!v*1]/[!v*2]/[!v*3]/[!v*4]/[!v*5]",
            ColouredTitle = "武当派<color=#FFCC00>長老</color>",
            TitleName = new[] { "武当", "Wudang" },
            ColouredLabel = "会心:\n<color=#FFA500>[攻]</color>ダメージ倍増",
            SpeakerLabel = "<color=#f3e584>トーマス[心の声]: </color>薬品の臭いがきつすぎる。息が詰まりそうだ。",
            SpeakerLabelWord = "[心の声]",
            Paragraph =
                "<color=#FFCC00>警告</color>\nリアクターは定格出力の [!v*0] パーセントで稼働中です。"
                + "次のジャンプの前に冷却材を排出してください。さもなければ乗組員は助かりません。"
                + "修理には [!v*1] クレジットと [!v*2] サイクルが必要で、その間は他に何も建造できません。",
            ParagraphFull =
                "<color=#FFCC00>回収報告</color>\n[!STR*0] を [!v*0] 番ベイの残骸から回収しました。\n\n"
                + "修理には [!v*1] クレジットと [!v*2] サイクルが必要です。"
                + "リアクターが [!v*3] パーセントを下回っている間、乗組員は作業できません。",
            ToneMarked = "まあ、みんなの予想どおりの結果だ。",
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
            TwoPlaceholders = "[!v*0]을 눌러 저장\nAPI 키가 필요합니다",
            // The particle trap: 은/는 and 이/가 depend on the final sound of the word before them,
            // which a placeholder hides. No choice is right, and that is the test.
            NameInjected = "[!STR*0]이(가) 승무원으로 합류했습니다",
            BlankLine = "목표 완료\n\n함선으로 복귀하십시오",
            TrailingBreak = "설정이 저장되었습니다\n",
            MarkupSpan = "<color=#FFCC00>경고</color>: 방어막 [!v*0] 퍼센트",
            MarkersInRow = "<color=#FFCC00>장전됨</color> [!v*0]/[!v*1]/[!v*2]/[!v*3]/[!v*4]/[!v*5]",
            ColouredTitle = "무당파 <color=#FFCC00>장로</color>",
            TitleName = new[] { "무당", "Wudang" },
            ColouredLabel = "치명타:\n<color=#FFA500>[공격]</color>피해 두 배",
            SpeakerLabel = "<color=#f3e584>토마스[생각]: </color>약품 냄새가 너무 독해. 숨이 막힐 것 같아.",
            SpeakerLabelWord = "[생각]",
            Paragraph =
                "<color=#FFCC00>경고</color>\n원자로가 정격 출력의 [!v*0] 퍼센트로 작동 중입니다. "
                + "다음 도약 전에 냉각수를 배출하십시오. 그렇지 않으면 승무원은 살아남지 못합니다. "
                + "수리에는 [!v*1] 크레딧과 [!v*2] 주기가 필요하며, 그동안 다른 것은 건조할 수 없습니다.",
            ParagraphFull =
                "<color=#FFCC00>인양 보고</color>\n[!v*0]번 격납고의 잔해에서 [!STR*0]을(를) 회수했습니다.\n\n"
                + "수리에는 [!v*1] 크레딧과 [!v*2] 주기가 필요합니다. "
                + "원자로가 [!v*3] 퍼센트 아래로 유지되는 동안 승무원은 작업할 수 없습니다.",
            ToneMarked = "뭐, 다들 예상한 대로였다.",
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
            TwoPlaceholders = "اضغط [!v*0] للحفظ\nمفتاح API مطلوب",
            NameInjected = "انضم [!STR*0] إلى الطاقم",
            BlankLine = "اكتمل الهدف\n\nعد إلى السفينة",
            TrailingBreak = "تم حفظ الإعدادات\n",
            MarkupSpan = "<color=#FFCC00>تحذير</color>: الدروع عند [!v*0] بالمئة",
            MarkersInRow = "<color=#FFCC00>تم التحميل</color> [!v*0]/[!v*1]/[!v*2]/[!v*3]/[!v*4]/[!v*5]",
            ColouredTitle = "طائفة وودانغ <color=#FFCC00>الشيخ</color>",
            TitleName = new[] { "وودانغ", "Wudang" },
            ColouredLabel = "ضربة حرجة:\n<color=#FFA500>[هجوم]</color>ضرر مضاعف",
            SpeakerLabel = "<color=#f3e584>توماس[تفكير]: </color>المواد الكيميائية قوية جدًا. أكاد أختنق هنا.",
            SpeakerLabelWord = "[تفكير]",
            Paragraph =
                "<color=#FFCC00>تحذير</color>\nيعمل المفاعل عند [!v*0] بالمئة من طاقته المقررة. "
                + "أفرغ سائل التبريد قبل القفزة التالية وإلا فلن ينجو الطاقم. "
                + "تكلف الإصلاحات [!v*1] رصيدًا وتستغرق [!v*2] دورات، ولا يمكن بناء أي شيء آخر خلالها.",
            ParagraphFull =
                "<color=#FFCC00>تقرير الإنقاذ</color>\nتم انتشال [!STR*0] من الحطام في الحوض "
                + "[!v*0].\n\nتكلف الإصلاحات [!v*1] رصيدًا وتستغرق [!v*2] دورات. "
                + "لا يستطيع الطاقم العمل ما دام المفاعل دون [!v*3] بالمئة.",
            ToneMarked = "حسنًا، جرى الأمر تمامًا كما توقع الجميع.",
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
