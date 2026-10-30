using System.ComponentModel;
using System.Globalization;

namespace DevShell.Services;

/// <summary>
/// Every word DevShell shows, in the five languages it ships with. Windows picks the language, and
/// the settings popup can override it; both go through <see cref="Apply"/>, which raises one change
/// notification so anything bound in XAML re-reads its text without a restart.
///
/// A key with no entry in the chosen language falls back to English and then to the key itself, so a
/// string that has not been translated yet shows up as English rather than as an error.
/// </summary>
public sealed class Strings : INotifyPropertyChanged
{
    /// <summary>"auto" follows Windows, otherwise one of the codes in <see cref="Available"/>.</summary>
    public const string Automatic = "auto";

    private static readonly Dictionary<string, string[]> Table = new(StringComparer.Ordinal)
    {
        // The bar itself.
        ["Bar_Start"] = ["Start", "Пуск", "Start", "Démarrer", "Inicio"],
        ["Bar_Search"] = ["Search apps and files on this PC", "Поиск приложений и файлов на этом ПК", "Apps und Dateien auf diesem PC suchen", "Rechercher des applications et des fichiers sur ce PC", "Buscar aplicaciones y archivos en este equipo"],
        ["Bar_Clear"] = ["Clear", "Очистить", "Löschen", "Effacer", "Borrar"],
        ["Bar_Running"] = ["Background apps", "Фоновые приложения", "Apps im Hintergrund", "Applications en arrière-plan", "Aplicaciones en segundo plano"],
        ["Bar_Clock"] = ["Date and time", "Дата и время", "Datum und Uhrzeit", "Date et heure", "Fecha y hora"],
        ["Bar_Network"] = ["Network", "Сеть", "Netzwerk", "Réseau", "Red"],
        ["Bar_Volume"] = ["Volume", "Звук", "Lautstärke", "Son", "Volumen"],
        ["Bar_Activity"] = ["Network activity - right-click for rate units", "Сетевой обмен — правый щелчок меняет единицы", "Netzwerkaktivität – Rechtsklick für Einheiten", "Activité réseau – clic droit pour les unités", "Actividad de red: clic derecho para las unidades"],
        ["Bar_Keyboard"] = ["Keyboard language", "Язык клавиатуры", "Tastatursprache", "Langue du clavier", "Idioma del teclado"],
        ["Bar_CloseWindow"] = ["Close window", "Закрыть окно", "Fenster schließen", "Fermer la fenêtre", "Cerrar ventana"],
        ["Bar_Checking"] = ["checking", "проверка", "wird geprüft", "vérification", "comprobando"],

        // The bar's own menu.
        ["Menu_RebuildIndex"] = ["Rebuild file index", "Пересобрать индекс файлов", "Dateiindex neu erstellen", "Reconstruire l'index des fichiers", "Reconstruir el índice de archivos"],
        ["Menu_DockOverlay"] = ["Dock: overlay the system taskbar", "Закрепить: поверх системной панели", "Andocken: über der Systemleiste", "Ancrer : par-dessus la barre système", "Acoplar: sobre la barra del sistema"],
        ["Menu_DockAppBar"] = ["Dock: reserve screen space", "Закрепить: зарезервировать место на экране", "Andocken: Bildschirmplatz reservieren", "Ancrer : réserver l'espace écran", "Acoplar: reservar espacio en pantalla"],
        ["Menu_Exit"] = ["Exit DevShell", "Выйти из DevShell", "DevShell beenden", "Quitter DevShell", "Salir de DevShell"],
        ["Pin_Add"] = ["Pin to taskbar", "Закрепить на панели задач", "An die Taskleiste anheften", "Épingler à la barre des tâches", "Anclar a la barra de tareas"],
        ["Pin_Remove"] = ["Unpin from taskbar", "Открепить от панели задач", "Von der Taskleiste lösen", "Détacher de la barre des tâches", "Desanclar de la barra de tareas"],
        ["Pin_MissingSource"] = ["That program is no longer where it was.", "Этой программы больше нет на прежнем месте.", "Das Programm liegt nicht mehr an seinem Platz.", "Ce programme n'est plus à son emplacement.", "Ese programa ya no está en su ubicación."],
        ["Pin_NotAPin"] = ["That shortcut is not in the taskbar folder.", "Этот ярлык не лежит в папке панели задач.", "Diese Verknüpfung liegt nicht im Taskleistenordner.", "Ce raccourci n'est pas dans le dossier de la barre des tâches.", "Ese acceso directo no está en la carpeta de la barra de tareas."],
        ["Pin_Failed"] = ["Could not change the taskbar pins.", "Не удалось изменить закреплённые программы.", "Die angehefteten Apps konnten nicht geändert werden.", "Impossible de modifier les épingles de la barre des tâches.", "No se pudo cambiar lo anclado a la barra de tareas."],
        ["Menu_Acrylic"] = ["Acrylic blur", "Акриловое размытие", "Acryl-Unschärfe", "Flou acrylique", "Desenfoque acrílico"],
        ["Menu_ShowDesktop"] = ["Show desktop layer", "Показать слой рабочего стола", "Desktop-Ebene anzeigen", "Afficher la couche bureau", "Mostrar la capa de escritorio"],
        ["Menu_TaskManager"] = ["Task Manager", "Диспетчер задач", "Task-Manager", "Gestionnaire des tâches", "Administrador de tareas"],
        ["Menu_OpenSettingsFolder"] = ["Open settings folder", "Открыть папку настроек", "Einstellungsordner öffnen", "Ouvrir le dossier des paramètres", "Abrir la carpeta de configuración"],
        ["Menu_RunTroubleshooter"] = ["Run Troubleshooter", "Запустить средство устранения неполадок", "Problembehandlung starten", "Lancer le dépannage", "Ejecutar el solucionador"],

        // The date flyout.
        ["Calendar_Today"] = ["Today: {0}", "Сегодня: {0}", "Heute: {0}", "Aujourd'hui : {0}", "Hoy: {0}"],
        ["Calendar_PreviousMonth"] = ["Previous month", "Предыдущий месяц", "Vorheriger Monat", "Mois précédent", "Mes anterior"],
        ["Calendar_NextMonth"] = ["Next month", "Следующий месяц", "Nächster Monat", "Mois suivant", "Mes siguiente"],

        // Search results.
        ["Search_TabApps"] = ["Apps", "Приложения", "Apps", "Applications", "Aplicaciones"],
        ["Search_TabFiles"] = ["Files", "Файлы", "Dateien", "Fichiers", "Archivos"],
        ["Search_Hint"] = ["Enter = open · Ctrl+Enter = as admin · right-click for more", "Enter — открыть · Ctrl+Enter — от администратора · правый щелчок — ещё", "Enter = öffnen · Strg+Enter = als Administrator · Rechtsklick für mehr", "Entrée = ouvrir · Ctrl+Entrée = en administrateur · clic droit pour plus", "Entrar = abrir · Ctrl+Entrar = como administrador · clic derecho para más"],
        ["Search_NoApp"] = ["No app matched \"{0}\"", "Нет приложений по запросу «{0}»", "Keine App gefunden für „{0}“", "Aucune application pour « {0} »", "Ninguna aplicación coincide con \"{0}\""],
        ["Search_NoIndex"] = ["No index yet - the file scan is still running. Results will appear as soon as it has something.", "Индекса пока нет — сканирование файлов ещё идёт. Результаты появятся, как только что-то найдётся.", "Noch kein Index – der Dateiscan läuft. Ergebnisse erscheinen, sobald er etwas gefunden hat.", "Pas encore d'index : l'analyse des fichiers est en cours. Les résultats apparaîtront dès qu'elle aura trouvé quelque chose.", "Todavía no hay índice: el análisis de archivos sigue en curso. Los resultados aparecerán en cuanto encuentre algo."],
        ["Search_NoMatch"] = ["Nothing matched \"{0}\"", "Ничего не найдено по запросу «{0}»", "Nichts gefunden für „{0}“", "Aucun résultat pour « {0} »", "Nada coincide con \"{0}\""],

        // Menus on a search result.
        ["Hit_Open"] = ["Open", "Открыть", "Öffnen", "Ouvrir", "Abrir"],
        ["Hit_Admin"] = ["Open as administrator", "Открыть от имени администратора", "Als Administrator öffnen", "Ouvrir en tant qu'administrateur", "Abrir como administrador"],
        ["Hit_Location"] = ["Open file location", "Открыть расположение файла", "Dateispeicherort öffnen", "Ouvrir l'emplacement du fichier", "Abrir la ubicación del archivo"],
        ["Hit_ShowFolder"] = ["Show folder", "Показать папку", "Ordner anzeigen", "Afficher le dossier", "Mostrar carpeta"],
        ["Hit_CopyPath"] = ["Copy full path", "Скопировать полный путь", "Vollständigen Pfad kopieren", "Copier le chemin complet", "Copiar la ruta completa"],

        // The Start menu.
        ["Start_AllApps"] = ["All apps", "Все приложения", "Alle Apps", "Toutes les applications", "Todas las aplicaciones"],
        ["Start_OtherApps"] = ["Other apps", "Другие приложения", "Weitere Apps", "Autres applications", "Otras aplicaciones"],
        ["Start_Settings"] = ["Settings", "Параметры", "Einstellungen", "Paramètres", "Configuración"],
        ["Start_Power"] = ["Power", "Завершение работы", "Energie", "Alimentation", "Energía"],
        ["Start_Account"] = ["Account", "Учётная запись", "Konto", "Compte", "Cuenta"],
        ["Start_DevShellSettings"] = ["DevShell settings", "Настройки DevShell", "DevShell-Einstellungen", "Paramètres de DevShell", "Configuración de DevShell"],
        ["Start_WindowsKeyHint"] = ["Windows key opens the Start menu", "Клавиша Windows открывает меню «Пуск»", "Windows-Taste öffnet das Startmenü", "La touche Windows ouvre le menu Démarrer", "La tecla Windows abre el menú Inicio"],
        ["Start_NothingToShow"] = ["Nothing to show", "Нечего показать", "Nichts anzuzeigen", "Rien à afficher", "Nada que mostrar"],
        ["Power_Sleep"] = ["Sleep", "Сон", "Energiesparmodus", "Veille", "Suspender"],
        ["Power_Restart"] = ["Restart", "Перезагрузка", "Neu starten", "Redémarrer", "Reiniciar"],
        ["Power_ShutDown"] = ["Shut down", "Завершение работы", "Herunterfahren", "Arrêter", "Apagar"],
        ["Power_SignOut"] = ["Sign out", "Выйти из системы", "Abmelden", "Se déconnecter", "Cerrar sesión"],

        // The DevShell page of the Start menu.
        ["Info_Title"] = ["DevShell", "DevShell", "DevShell", "DevShell", "DevShell"],
        ["Info_Tagline"] = ["A Windows shell, written from scratch", "Оболочка Windows, написанная с нуля", "Eine Windows-Shell, von Grund auf geschrieben", "Un shell Windows écrit de zéro", "Un shell de Windows escrito desde cero"],
        ["Info_Technical"] = ["TECHNICAL", "ТЕХНИЧЕСКИЕ ДАННЫЕ", "TECHNIK", "TECHNIQUE", "TÉCNICO"],
        ["Info_Version"] = ["Version", "Версия", "Version", "Version", "Versión"],
        ["Info_Machine"] = ["Machine", "Компьютер", "Computer", "Machine", "Equipo"],
        ["Info_Display"] = ["Display", "Экран", "Anzeige", "Écran", "Pantalla"],
        ["Info_Runtime"] = ["Runtime", "Среда выполнения", "Laufzeit", "Runtime", "Tiempo de ejecución"],
        ["Info_Architecture"] = ["Architecture", "Архитектура", "Architektur", "Architecture", "Arquitectura"],
        ["Info_Process"] = ["Process", "Процесс", "Prozess", "Processus", "Proceso"],
        ["Info_Assembly"] = ["Assembly", "Сборка", "Assembly", "Assembly", "Ensamblado"],
        ["Info_RunningSince"] = ["Running since", "Работает с", "Läuft seit", "Actif depuis", "En ejecución desde"],
        ["Info_Bar"] = ["Bar", "Панель", "Leiste", "Barre", "Barra"],
        ["Info_WindowsMode"] = ["Windows mode", "Режим Windows", "Windows-Modus", "Mode Windows", "Modo de Windows"],
        ["Info_ShellMode"] = ["DevShell is the shell", "DevShell — оболочка", "DevShell ist die Shell", "DevShell est le shell", "DevShell es el shell"],
        ["Info_Alongside"] = ["Running alongside Explorer", "Работает рядом с Explorer", "Läuft neben dem Explorer", "Fonctionne avec l'Explorateur", "Se ejecuta junto a Explorer"],
        ["Info_DataFolder"] = ["Data folder", "Папка данных", "Datenordner", "Dossier de données", "Carpeta de datos"],
        ["Info_CopyDetails"] = ["Copy details", "Скопировать данные", "Details kopieren", "Copier les détails", "Copiar los detalles"],
        ["Info_Copied"] = ["Copied", "Скопировано", "Kopiert", "Copié", "Copiado"],

        // The settings popup.
        ["Settings_Title"] = ["DevShell settings", "Настройки DevShell", "DevShell-Einstellungen", "Paramètres de DevShell", "Configuración de DevShell"],
        ["Settings_Desktop"] = ["DESKTOP", "РАБОЧИЙ СТОЛ", "DESKTOP", "BUREAU", "ESCRITORIO"],
        ["Settings_ShowDesktopIcons"] = ["Show desktop icons", "Показывать значки рабочего стола", "Desktop-Symbole anzeigen", "Afficher les icônes du bureau", "Mostrar iconos del escritorio"],
        ["Settings_IncludePublic"] = ["Include the Public desktop", "Включать общий рабочий стол", "Öffentlichen Desktop einbeziehen", "Inclure le bureau public", "Incluir el escritorio público"],
        ["Settings_ShowRecycleBin"] = ["Show the Recycle Bin", "Показывать корзину", "Papierkorb anzeigen", "Afficher la Corbeille", "Mostrar la Papelera de reciclaje"],
        ["Settings_ShowHidden"] = ["Show hidden items", "Показывать скрытые элементы", "Ausgeblendete Elemente anzeigen", "Afficher les éléments masqués", "Mostrar elementos ocultos"],
        ["Settings_ShowExtensions"] = ["Show file extensions", "Показывать расширения файлов", "Dateierweiterungen anzeigen", "Afficher les extensions de fichier", "Mostrar extensiones de archivo"],
        ["Settings_IconSize"] = ["Icon size", "Размер значков", "Symbolgröße", "Taille des icônes", "Tamaño de los iconos"],
        ["Settings_Arrange"] = ["Arrange icons", "Упорядочить значки", "Symbole anordnen", "Organiser les icônes", "Organizar iconos"],
        ["Settings_Refresh"] = ["Refresh", "Обновить", "Aktualisieren", "Actualiser", "Actualizar"],
        ["Settings_Folder"] = ["Settings folder", "Папка настроек", "Einstellungsordner", "Dossier des paramètres", "Carpeta de configuración"],
        ["Settings_Bar"] = ["BAR", "ПАНЕЛЬ", "LEISTE", "BARRE", "BARRA"],
        ["Settings_Acrylic"] = ["Acrylic blur", "Акриловое размытие", "Acryl-Unschärfe", "Flou acrylique", "Desenfoque acrílico"],
        ["Settings_Seconds"] = ["Seconds in the clock", "Секунды в часах", "Sekunden in der Uhr", "Secondes dans l'horloge", "Segundos en el reloj"],
        ["Settings_Transparency"] = ["Transparency", "Прозрачность", "Transparenz", "Transparence", "Transparencia"],
        ["Settings_Windows"] = ["WINDOWS", "WINDOWS", "WINDOWS", "WINDOWS", "WINDOWS"],
        ["Settings_ShellMode"] = ["DevShell is the shell", "DevShell — оболочка", "DevShell ist die Shell", "DevShell est le shell", "DevShell es el shell"],
        ["Settings_Alongside"] = ["Run alongside Explorer", "Работать рядом с Explorer", "Neben dem Explorer laufen", "Fonctionner avec l'Explorateur", "Ejecutar junto a Explorer"],
        ["Settings_ShellHint"] = ["Changes the Shell value for this account. Sign out and back in so Windows starts the one you picked.", "Меняет значение Shell для этой учётной записи. Выйдите и войдите снова, чтобы Windows запустила выбранный вариант.", "Ändert den Shell-Wert für dieses Konto. Ab- und wieder anmelden, damit Windows den gewählten Start verwendet.", "Modifie la valeur Shell de ce compte. Déconnectez-vous puis reconnectez-vous pour que Windows démarre le choix retenu.", "Cambia el valor Shell de esta cuenta. Cierre y vuelva a iniciar sesión para que Windows arranque el elegido."],
        ["Settings_ShellHintOn"] = ["Windows starts DevShell instead of Explorer when you sign in, and shortcuts to Explorer open a file window. This takes effect the next time you sign in.", "Windows запускает DevShell вместо Explorer при входе, а ярлыки Explorer открывают окно файлов. Изменение вступит в силу при следующем входе.", "Windows startet DevShell statt des Explorers bei der Anmeldung, und Explorer-Verknüpfungen öffnen ein Dateifenster. Wirksam ab der nächsten Anmeldung.", "Windows démarre DevShell au lieu de l'Explorateur à l'ouverture de session, et les raccourcis de l'Explorateur ouvrent une fenêtre de fichiers. Effectif à la prochaine connexion.", "Windows inicia DevShell en lugar del Explorador al iniciar sesión, y los accesos directos del Explorador abren una ventana de archivos. Se aplica en el próximo inicio de sesión."],
        ["Settings_ShellHintOff"] = ["Windows keeps its own desktop and taskbar, and DevShell's bar starts with your session. The desktop layer above is for putting DevShell's own icons on the wallpaper.", "Windows сохраняет свой рабочий стол и панель задач, а панель DevShell запускается вместе с сеансом. Слой рабочего стола выше нужен, чтобы разместить значки DevShell на обоях.", "Windows behält Desktop und Taskleiste, und die DevShell-Leiste startet mit der Sitzung. Die Desktop-Ebene oben legt DevShells eigene Symbole auf das Hintergrundbild.", "Windows conserve son bureau et sa barre des tâches, et la barre DevShell démarre avec la session. La couche bureau ci-dessus place les icônes DevShell sur le fond d'écran.", "Windows mantiene su escritorio y su barra de tareas, y la barra de DevShell se inicia con la sesión. La capa de escritorio de arriba pone los iconos de DevShell sobre el fondo."],
        ["Settings_Troubleshooters"] = ["TROUBLESHOOTERS", "УСТРАНЕНИЕ НЕПОЛАДОК", "PROBLEMBEHANDLUNG", "DÉPANNAGE", "SOLUCIÓN DE PROBLEMAS"],
        ["Settings_Classic"] = ["Classic (msdt)", "Классические (msdt)", "Klassisch (msdt)", "Classiques (msdt)", "Clásicos (msdt)"],
        ["Settings_GetHelp"] = ["Get Help app (Windows 11)", "Приложение «Поддержка» (Windows 11)", "Get Help-App (Windows 11)", "Application Obtenir de l'aide (Windows 11)", "Aplicación Obtener ayuda (Windows 11)"],
        ["Settings_TroubleshooterHintClassic"] = ["Classic msdt packs, the ones Windows 10 shipped with. Windows 11 has started removing them; if one is gone, DevShell falls back to Get Help.", "Классические пакеты msdt, как в Windows 10. В Windows 11 их начали убирать; если пакета нет, DevShell откроет «Поддержку».", "Klassische msdt-Pakete aus Windows 10. Windows 11 entfernt sie nach und nach; fehlt eines, weicht DevShell auf Get Help aus.", "Les packs msdt classiques de Windows 10. Windows 11 commence à les retirer ; s'il en manque un, DevShell bascule sur Obtenir de l'aide.", "Los paquetes msdt clásicos de Windows 10. Windows 11 empieza a quitarlos; si falta uno, DevShell usa Obtener ayuda."],
        ["Settings_TroubleshooterHintGetHelp"] = ["Opens the item's own troubleshooter inside the Get Help app, the same one the Get Help app's \"Windows troubleshooters\" list runs. If Get Help is not installed, DevShell falls back to the classic pack.", "Открывает средство устранения неполадок внутри приложения «Поддержка» — то же, что запускает его список «Средства устранения неполадок Windows». Если «Поддержки» нет, DevShell откроет классический пакет.", "Öffnet die Problembehandlung des Elements in der Get Help-App – dieselbe, die deren Liste „Windows-Problembehandlungen“ startet. Fehlt Get Help, weicht DevShell auf das klassische Paket aus.", "Ouvre le module de dépannage de l'élément dans l'application Obtenir de l'aide, celui que lance sa liste « Résolution des problèmes Windows ». Sans cette application, DevShell revient au pack classique.", "Abre el solucionador del elemento dentro de la aplicación Obtener ayuda, el mismo que lanza su lista \"Solucionadores de problemas de Windows\". Si no está instalada, DevShell usa el paquete clásico."],
        ["Settings_Language"] = ["LANGUAGE", "ЯЗЫК", "SPRACHE", "LANGUE", "IDIOMA"],
        ["Settings_LanguageAuto"] = ["Follow Windows", "Как в Windows", "Wie in Windows", "Comme Windows", "Como en Windows"],

        // The network flyout.
        ["Status_NotConnected"] = ["Not connected", "Нет подключения", "Nicht verbunden", "Non connecté", "Sin conexión"],
        ["Wifi_Title"] = ["Wi-Fi", "Wi-Fi", "WLAN", "Wi-Fi", "Wi-Fi"],
        ["Wifi_Scan"] = ["Scan for networks", "Поиск сетей", "Netzwerke suchen", "Rechercher des réseaux", "Buscar redes"],
        ["Wifi_Connect"] = ["Connect", "Подключить", "Verbinden", "Se connecter", "Conectar"],
        ["Wifi_Cancel"] = ["Cancel", "Отмена", "Abbrechen", "Annuler", "Cancelar"],
        ["Wifi_Adapters"] = ["Adapters", "Адаптеры", "Adapter", "Cartes réseau", "Adaptadores"],
        ["Wifi_Scanning"] = ["Scanning…", "Поиск…", "Wird gesucht…", "Recherche…", "Buscando…"],
        ["Wifi_ScanFailed"] = ["The scan failed.", "Не удалось выполнить поиск.", "Die Suche ist fehlgeschlagen.", "La recherche a échoué.", "La búsqueda ha fallado."],
        ["Wifi_NoneInRange"] = ["No networks in range", "В радиусе нет сетей", "Keine Netzwerke in Reichweite", "Aucun réseau à portée", "No hay redes al alcance"],
        ["Wifi_EnterPassword"] = ["Enter the network password.", "Введите пароль сети.", "Netzwerkkennwort eingeben.", "Saisissez le mot de passe du réseau.", "Escriba la contraseña de la red."],
        ["Wifi_OpenNetwork"] = ["This network is open: anything sent over it can be read by anyone nearby.", "Эта сеть открытая: всё, что по ней передаётся, может прочитать кто угодно поблизости.", "Dieses Netzwerk ist offen: Alles, was darüber gesendet wird, kann jeder in der Nähe mitlesen.", "Ce réseau est ouvert : tout ce qui y circule peut être lu par n'importe qui à proximité.", "Esta red está abierta: cualquiera cerca puede leer lo que se envíe por ella."],
        ["Wifi_AlreadySaved"] = ["Windows already has this network saved.", "Windows уже сохранил эту сеть.", "Windows hat dieses Netzwerk bereits gespeichert.", "Windows a déjà enregistré ce réseau.", "Windows ya tiene guardada esta red."],
        ["Wifi_ConnectedTo"] = ["Connected to {0} · {1}%", "Подключено к {0} · {1}%", "Verbunden mit {0} · {1} %", "Connecté à {0} · {1} %", "Conectado a {0} · {1} %"],
        ["Net_Activity"] = ["Network activity", "Сетевой обмен", "Netzwerkaktivität", "Activité réseau", "Actividad de red"],
        ["Net_RateUnits"] = ["Rate units", "Единицы скорости", "Einheiten", "Unités de débit", "Unidades de velocidad"],
        ["Net_RateBytes"] = ["Bytes  (KB/s, MB/s)", "Байты  (КБ/с, МБ/с)", "Bytes  (KB/s, MB/s)", "Octets  (Ko/s, Mo/s)", "Bytes  (KB/s, MB/s)"],
        ["Net_RateBits"] = ["Bits  (Kbit/s, Mbit/s)", "Биты  (Кбит/с, Мбит/с)", "Bits  (Kbit/s, Mbit/s)", "Bits  (Kbit/s, Mbit/s)", "Bits  (Kbit/s, Mbit/s)"],
        ["Net_Bytes"] = ["Bytes", "Байты", "Bytes", "Octets", "Bytes"],
        ["Net_Bits"] = ["Bits", "Биты", "Bits", "Bits", "Bits"],

        // The volume flyout.
        ["Audio_Muted"] = ["Muted", "Без звука", "Stumm", "Son coupé", "Silenciado"],
        ["Audio_VolumePercent"] = ["Volume {0:0}%", "Звук {0:0}%", "Lautstärke {0:0} %", "Son {0:0} %", "Volumen {0:0} %"],
        ["Audio_DefaultDevice"] = ["Default output device", "Устройство вывода по умолчанию", "Standard-Ausgabegerät", "Périphérique de sortie par défaut", "Dispositivo de salida predeterminado"],
        ["Audio_Mute"] = ["Mute", "Выключить звук", "Stumm", "Couper le son", "Silenciar"],
        ["Audio_Unmute"] = ["Unmute", "Включить звук", "Ton an", "Rétablir le son", "Activar sonido"],
        ["Audio_Volume"] = ["Volume", "Звук", "Lautstärke", "Son", "Volumen"],

        // The notification-area flyout.
        ["Tray_Title"] = ["Background apps", "Фоновые приложения", "Apps im Hintergrund", "Applications en arrière-plan", "Aplicaciones en segundo plano"],
        ["Tray_Empty"] = ["Nothing is running in the background", "В фоне ничего не запущено", "Im Hintergrund läuft nichts", "Rien ne s'exécute en arrière-plan", "No hay nada en segundo plano"],

        // A running app's preview menu.
        ["Run_Minimize"] = ["Minimize", "Свернуть", "Minimieren", "Réduire", "Minimizar"],
        ["Run_MinimizeAll"] = ["Minimize all windows", "Свернуть все окна", "Alle Fenster minimieren", "Réduire toutes les fenêtres", "Minimizar todas las ventanas"],
        ["Run_Close"] = ["Close window", "Закрыть окно", "Fenster schließen", "Fermer la fenêtre", "Cerrar ventana"],
        ["Run_CloseAll"] = ["Close all windows", "Закрыть все окна", "Alle Fenster schließen", "Fermer toutes les fenêtres", "Cerrar todas las ventanas"],

        // The desktop layer.
        ["Desktop_Title"] = ["DevShell desktop", "Рабочий стол DevShell", "DevShell-Desktop", "Bureau DevShell", "Escritorio de DevShell"],
        ["Desk_OpenPersonal"] = ["Open personal folder", "Открыть личную папку", "Persönlichen Ordner öffnen", "Ouvrir le dossier personnel", "Abrir la carpeta personal"],
        ["Desk_OpenPublic"] = ["Open public folder", "Открыть общую папку", "Öffentlichen Ordner öffnen", "Ouvrir le dossier public", "Abrir la carpeta pública"],
        ["Desk_ShowIcons"] = ["Show desktop icons", "Показывать значки рабочего стола", "Desktop-Symbole anzeigen", "Afficher les icônes du bureau", "Mostrar iconos del escritorio"],
        ["Desk_IncludePublic"] = ["Include public desktop items", "Включать элементы общего рабочего стола", "Elemente des öffentlichen Desktops einbeziehen", "Inclure les éléments du bureau public", "Incluir elementos del escritorio público"],
        ["Desk_ShowRecycleBin"] = ["Show Recycle Bin", "Показывать корзину", "Papierkorb anzeigen", "Afficher la Corbeille", "Mostrar la Papelera"],
        ["Desk_ShowHidden"] = ["Show hidden items", "Показывать скрытые элементы", "Versteckte Elemente anzeigen", "Afficher les éléments masqués", "Mostrar elementos ocultos"],
        ["Desk_Refresh"] = ["Refresh desktop icons", "Обновить значки рабочего стола", "Desktop-Symbole aktualisieren", "Actualiser les icônes du bureau", "Actualizar los iconos del escritorio"],
        ["Desk_WallpaperWindows"] = ["Use the Windows wallpaper", "Использовать обои Windows", "Windows-Hintergrund verwenden", "Utiliser le fond d'écran Windows", "Usar el fondo de Windows"],
        ["Desk_ChangeWallpaper"] = ["Change wallpaper…", "Сменить обои…", "Hintergrundbild ändern…", "Changer le fond d'écran…", "Cambiar el fondo…"],
        ["Desk_ChooseWallpaper"] = ["Choose a wallpaper", "Выбрать обои", "Hintergrundbild auswählen", "Choisir un fond d'écran", "Elegir un fondo"],
        ["Desk_Arrange"] = ["Arrange icons", "Упорядочить значки", "Symbole anordnen", "Organiser les icônes", "Organizar iconos"],

        // Dialogs and the few message boxes.
        ["Dlg_Rename"] = ["Rename", "Переименовать", "Umbenennen", "Renommer", "Cambiar nombre"],
        ["Dlg_RenamePrompt"] = ["Rename '{0}'", "Переименовать «{0}»", "„{0}“ umbenennen", "Renommer « {0} »", "Cambiar el nombre de \"{0}\""],
        ["Dlg_OK"] = ["OK", "ОК", "OK", "OK", "Aceptar"],
        ["Dlg_Cancel"] = ["Cancel", "Отмена", "Abbrechen", "Annuler", "Cancelar"],
        ["Dlg_NameRequired"] = ["Enter a name.", "Введите имя.", "Geben Sie einen Namen ein.", "Saisissez un nom.", "Escriba un nombre."],
        ["Dlg_NameInvalidChars"] = ["A name cannot contain any of \\ / : * ? \" < > |.", "Имя не может содержать \\ / : * ? \" < > |.", "Ein Name darf keines dieser Zeichen enthalten: \\ / : * ? \" < > |.", "Un nom ne peut pas contenir \\ / : * ? \" < > |.", "Un nombre no puede contener \\ / : * ? \" < > |."],
        ["Dlg_NameTrailing"] = ["A name cannot end with a space or a period.", "Имя не может заканчиваться пробелом или точкой.", "Ein Name darf nicht mit einem Leerzeichen oder Punkt enden.", "Un nom ne peut pas se terminer par un espace ou un point.", "Un nombre no puede terminar en espacio ni punto."],
        ["Dlg_RenameFailed"] = ["Could not rename '{0}'.\n\n{1}", "Не удалось переименовать «{0}».\n\n{1}", "„{0}“ konnte nicht umbenannt werden.\n\n{1}", "Impossible de renommer « {0} ».\n\n{1}", "No se pudo cambiar el nombre de \"{0}\".\n\n{1}"],
        ["Dlg_NameTaken"] = ["There is already an item called '{0}' here.", "Здесь уже есть элемент «{0}».", "Hier gibt es bereits ein Element namens „{0}“.", "Un élément nommé « {0} » existe déjà ici.", "Ya hay un elemento llamado \"{0}\" aquí."],
        ["Msg_ThisPc"] = ["This PC", "Этот компьютер", "Dieser PC", "Ce PC", "Este equipo"],
        ["Msg_Settings"] = ["Settings", "Параметры", "Einstellungen", "Paramètres", "Configuración"],
        ["Msg_SettingsFailed"] = ["Windows Settings could not be opened.", "Не удалось открыть «Параметры» Windows.", "Die Windows-Einstellungen konnten nicht geöffnet werden.", "Impossible d'ouvrir les paramètres Windows.", "No se pudo abrir Configuración de Windows."],
        ["Msg_DirElevation"] = ["Directories cannot be opened elevated.", "Папки нельзя открыть от имени администратора.", "Ordner können nicht erhöht geöffnet werden.", "Les dossiers ne peuvent pas être ouverts avec élévation.", "Las carpetas no se pueden abrir con permisos elevados."],
        ["Msg_ElevationFailed"] = ["Elevation failed or was declined.", "Не удалось повысить права или запрос отклонён.", "Erhöhung fehlgeschlagen oder abgelehnt.", "Élévation impossible ou refusée.", "No se pudo elevar o se denegó."],
        ["Msg_FileGone"] = ["File no longer exists.", "Файла больше нет.", "Die Datei existiert nicht mehr.", "Le fichier n'existe plus.", "El archivo ya no existe."],
        ["Msg_TaskManagerFailed"] = ["Task Manager could not be started.", "Не удалось запустить диспетчер задач.", "Der Task-Manager konnte nicht gestartet werden.", "Impossible de démarrer le Gestionnaire des tâches.", "No se pudo iniciar el Administrador de tareas."],
    };

    private static readonly string[] Codes = ["en", "ru", "de", "fr", "es"];

    private string _code = "en";

    private Strings()
    {
    }

    /// <summary>The table itself, for XAML bindings such as Text="{l:Tr Bar_Start}".</summary>
    public static Strings Instance { get; } = new();

    /// <summary>Raised after the language changed, for the few texts assigned in code.</summary>
    public static event Action? Changed;

    /// <summary>The language in force, as a two-letter code.</summary>
    public static string Code => Instance._code;

    /// <summary>The choices the settings popup offers, native names and all.</summary>
    public static (string Code, string Name)[] Available { get; } =
    [
        (Automatic, "Follow Windows"),
        ("en", "English"),
        ("ru", "Русский"),
        ("de", "Deutsch"),
        ("fr", "Français"),
        ("es", "Español"),
    ];

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The text for a key in the language in force, English when it is missing.</summary>
    public string this[string key] => Text(key);

    /// <summary>Shortcut for code that builds menus and dialogs by hand.</summary>
    public static string T(string key) => Instance.Text(key);

    /// <summary>For the strings that carry a placeholder, such as Search_NoMatch.</summary>
    public static string Format(string key, params object?[] args)
    {
        var text = T(key);

        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, args);
        }
        catch (FormatException)
        {
            // A translated string that lost its {0} must not take the search box down with it.
            return text;
        }
    }

    /// <summary>
    /// Applies the stored setting: "auto" follows the Windows display language, anything else must
    /// be one of <see cref="Codes"/>. Unknown values fall back to English rather than to nothing.
    /// </summary>
    public static void Apply(string? setting)
    {
        var code = Resolve(setting);

        if (string.Equals(code, Instance._code, StringComparison.Ordinal))
        {
            return;
        }

        Instance._code = code;

        // "Item[]" is how WPF spells "every indexer result changed", which is what the bindings read.
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(nameof(Code)));
        Changed?.Invoke();
    }

    /// <summary>The code a setting resolves to, without applying it.</summary>
    public static string Resolve(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting) || setting == Automatic)
        {
            return FromWindows();
        }

        var code = setting.Trim().ToLowerInvariant();
        return Codes.Contains(code) ? code : "en";
    }

    private static string FromWindows()
    {
        try
        {
            var code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            return Codes.Contains(code) ? code : "en";
        }
        catch (Exception)
        {
            return "en";
        }
    }

    private string Text(string key)
    {
        if (!Table.TryGetValue(key, out var values))
        {
            return key;
        }

        var index = Array.IndexOf(Codes, _code);
        return index >= 0 && !string.IsNullOrEmpty(values[index]) ? values[index] : values[0];
    }
}
