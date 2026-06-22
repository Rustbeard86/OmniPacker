const tabs = document.querySelectorAll(".tab");
const panels = document.querySelectorAll(".tab-content");
const branchToggle = document.getElementById("branch-password-toggle");
const branchPassword = document.getElementById("branch-password");
const qrLoginToggle = document.getElementById("qr-login-toggle");
const appIdInput = document.getElementById("appid");
const osSelect = document.getElementById("os-select");
const branchInput = document.getElementById("branch");
const steamUsernameInput = document.getElementById("steam-username");
const steamPasswordInput = document.getElementById("steam-password");
const startButton = document.querySelector(".start-button");
const openOutputButton = document.querySelector(".open-output-button");
const addToQueueButton = document.querySelector(".add-to-queue");
const clearQueueButton = document.querySelector(".queue-clear-button");
const queueList = document.getElementById("queue-list");
const consoleOutput = document.querySelector(".console-output");
const qrModalOverlay = document.querySelector(".qr-modal-overlay");
const qrModalOutput = document.querySelector(".qr-modal-output");
const qrCopyButton = document.querySelector(".qr-copy-button");
const qrCloseButton = document.querySelector(".qr-close-button");
const settingsButton = document.querySelector(".settings-button");
const settingsModalOverlay = document.querySelector(".settings-modal-overlay");
const settingsCloseButton = document.querySelector(".settings-close-button");
const skipCompressionToggle = document.getElementById("skip-compression-toggle");
const compressionPasswordToggle = document.getElementById(
  "compression-password-toggle",
);
const compressionPasswordInput = document.getElementById(
  "compression-password-input",
);
const customCompressionArgsInput = document.getElementById("custom-compression-args");
const defaultQrToggle = document.getElementById("default-qr-toggle");
const languageSelect = document.getElementById("language-select");
const saveLoginButton = document.querySelector(".save-login-button");
const deleteLoginButton = document.querySelector(".settings-delete-login-button");
const steamGuardModalOverlay = document.querySelector(".steam-guard-modal-overlay");
const steamGuardEmailOverlay = document.querySelector(".steam-guard-email-overlay");
const steamGuardEmailMessage = document.querySelector(".steam-guard-email-message");
const steamGuardEmailInput = document.querySelector(".steam-guard-email-input");
const steamGuardEmailSubmitButton = document.querySelector(
  ".steam-guard-email-submit",
);
const steamGuardEmailCloseButton = document.querySelector(".steam-guard-email-close");
const steamGuardEmailStatus = document.querySelector(".steam-guard-email-status");
const templateEditorButton = document.querySelector(".template-editor-button");
const templateModalOverlay = document.querySelector(".template-modal-overlay");
const templateCloseButton = document.querySelector(".template-close-button");
const templateBlocksContainer = document.querySelector(".template-blocks");
const templateBlockSelect = document.querySelector(".template-block-select");
const templateAddBlockButton = document.querySelector(".template-add-block-button");
const templateLoadButton = document.querySelector(".template-load-button");
const templateSaveButton = document.querySelector(".template-save-button");
const templateResetButton = document.querySelector(".template-reset-button");
const templateLoadInput = document.querySelector(".template-load-input");
const templateCopyButton = document.querySelector(".template-copy-button");
const templatePreviewOutput = document.querySelector(".template-preview-output");
const templatePreviewRendered = document.querySelector(".template-preview-rendered");
const templateRenderToggle = document.querySelector(".template-render-toggle");
const templatePreviewMeta = document.querySelector(".template-preview-meta");
const templateStatus = document.querySelector(".template-status");
const templateConfirmOverlay = document.querySelector(".template-confirm-overlay");
const templateConfirmMessage = document.querySelector(".template-confirm-message");
const templateConfirmYesButton = document.querySelector(".template-confirm-yes");
const templateConfirmNoButton = document.querySelector(".template-confirm-no");
const outputConflictOverlay = document.querySelector(".output-conflict-overlay");
const outputConflictMessage = document.querySelector(".output-conflict-message");
const outputConflictPath = document.querySelector(".output-conflict-path");
const outputConflictOverwriteButton = document.querySelector(
  ".output-conflict-overwrite",
);
const outputConflictCopyButton = document.querySelector(".output-conflict-copy");
const outputConflictCancelButton = document.querySelector(
  ".output-conflict-cancel",
);

const jobState = {
  jobs: new Map(),
  runningJobId: null,
  selectedJobId: null,
  order: [],
};

const authState = {
  rememberedUsername: null,
  savedLogin: null,
};

const settingsState = {
  skipCompression: false,
  compressionPasswordEnabled: false,
  compressionPassword: "",
  customCompressionArgs: "",
  defaultQrLogin: false,
  language: "en",
  defaultTemplate: null,
  lastTemplateSaveDir: null,
};

const outputConflictState = {
  jobId: null,
  outputName: "",
  outputPath: "",
  busy: false,
};

const steamGuardEmailState = {
  jobId: null,
  provider: "",
  busy: false,
};

const steamGuardEmailRetryState = {
  jobId: null,
  requested: false,
};

// Owned-app library picker (Library tab).
const libraryState = {
  apps: [], // [{ appid, name, type, branches: [{name, buildId, timeUpdated, pwdRequired}] }]
  loaded: false,
  loading: false,
  filter: "",
  // appid (string) -> { selected, branch, branchPassword }
  selection: new Map(),
  // appid string -> { autoArchive: bool } for watched apps
  watched: new Map(),
};

// Updates detected by the background watcher this session.
const updatesState = {
  items: [], // [{ appid, name, branch, buildId, previousBuildId }]
};

// Lightweight pseudo-job for the enumeration login flow so the existing QR /
// Steam Guard handlers can be reused without a real queue job.
const enumState = {
  active: false,
  qrEnabled: false,
  qrCaptureActive: false,
  qrCaptureLines: [],
  qrText: null,
  // Live debug log for the Library flow, surfaced in the Console tab.
  log: [],
  showInConsole: false,
};

const LOG_LINE_CAP = 10000;
const LOG_TRIM_MARGIN = 1000;
const CONSOLE_FLUSH_INTERVAL_MS = 120;

let activeTab =
  document.querySelector(".tab.active")?.dataset.tab || "queue";

const consoleRenderState = {
  jobId: null,
  renderedLines: 0,
  needsFullRender: true,
  timer: null,
};

const translations = {
  en: {
    "tab.queue": "Queue",
    "tab.console": "Console",
    "queue.label": "Queue:",
    "queue.clear": "Clear Queue",
    "queue.empty": "No downloads queued.",
    "queue.running": "Queue is running",
    "queue.noReorder": "Cannot reorder/remove while running",
    "queue.atTop": "Already at top",
    "queue.atBottom": "Already at bottom",
    "queue.appId": "AppID {{appId}}",
    "queue.meta": "Branch: {{branch}} • OS: {{os}}",
    "queue.status.queued": "queued",
    "queue.status.running": "running",
    "queue.status.compressing": "compressing",
    "queue.status.done": "done",
    "queue.status.failed": "failed",
    "auth.username": "Steam Username:",
    "auth.password": "Steam Password:",
    "auth.qr": "Use QR Login",
    "auth.save": "Save Login Details",
    "auth.saveMissing": "Enter both Steam username and password before saving.",
    "auth.saveFailed": "Failed to save login data: {{error}}",
    "auth.deleteFailed": "Failed to delete saved login data: {{error}}",
    "auth.loadFailed": "Failed to load saved login data: {{error}}",
    "auth.tauriUnavailable": "Tauri invoke API unavailable.",
    "auth.reuseQr": "[system] Reusing QR login for {{username}}.",
    "game.title": "Game Manager",
    "game.appid": "AppID:",
    "game.add": "Add to Queue",
    "game.os": "OS:",
    "branch.title": "Branch Manager",
    "branch.label": "Branch to download:",
    "branch.password": "Branch password? (Enter below)",
    "output.open": "Open Output Folder",
    "output.conflict.title": "Output already exists",
    "output.conflict.message": "An output named \"{{name}}\" already exists.",
    "output.conflict.path": "Location: {{path}}",
    "output.conflict.overwrite": "Overwrite",
    "output.conflict.copy": "Make Copy",
    "output.conflict.cancel": "Cancel",
    "output.conflict.log":
      "[system] Output already exists: {{path}}. Choose overwrite, copy, or cancel.",
    "output.conflict.choice.overwrite":
      "[system] Overwriting existing output.",
    "output.conflict.choice.copy":
      "[system] Creating a copy output.",
    "output.conflict.choice.cancel":
      "[system] Output conflict cancelled by user.",
    "output.conflict.resolveError":
      "[system] Output conflict response failed: {{error}}",
    "console.noSelection": "No job selected.",
    "console.noQueuedJobs": "No queued jobs.",
    "qr.title": "Steam QR Login",
    "qr.help": "Scan with Steam Mobile App",
    "qr.waiting": "Waiting for QR code output…",
    "settings.button": "Settings",
    "settings.title": "Settings",
    "settings.language": "Language:",
    "settings.skipCompression": "Skip compression after download",
    "settings.compressionPasswordToggle": "Use compression password",
    "settings.compressionPasswordLabel": "Compression password:",
    "settings.compressionPasswordRequired":
      "Compression password cannot be enabled without setting a password.",
    "settings.compressionPasswordHint":
      "Header encryption (-mhe=on) is enabled by default when a password is set.",
    "settings.customCompressionArgsLabel": "Custom 7-Zip arguments:",
    "settings.customCompressionArgsHint":
      "Extra flags passed to 7-Zip. OmniPacker auto-tunes -mmt and -md for your system; overriding them may cause issues. Flags -t, -p, and -bsp are not allowed.",
    "settings.defaultQrLogin": "Default to QR Login",
    "settings.deleteLogin": "Delete Saved Login Data",
    "settings.deleteNotImplemented": "This feature is not yet implemented.",
    "template.button": "Template Editor",
    "template.title": "Template Editor",
    "template.builder": "Block Builder",
    "template.preview": "Live Preview",
    "template.addBlock": "Add Block",
    "template.copy": "Copy BBCode",
    "template.save": "Save JSON",
    "template.load": "Load JSON",
    "template.reset": "Reset to Default",
    "template.block.title": "Title Block",
    "template.block.version": "Version Block",
    "template.block.depot_list": "Depot List Block",
    "template.block.free_text": "Free Text Block",
    "template.block.uploaded_version": "Uploaded Version Block",
    "template.block.patch_notes": "Patch Notes Block",
    "template.field.template": "Template",
    "template.field.text": "Text",
    "template.field.depotTitle": "Spoiler title",
    "template.field.depotLine": "Depot line template",
    "template.field.useCode": "Wrap in [code=text]",
    "template.action.up": "Move up",
    "template.action.down": "Move down",
    "template.action.remove": "Remove",
    "template.status.ready": "Template ready.",
    "template.status.loaded": "Template loaded.",
    "template.status.saved": "Template saved.",
    "template.status.reset": "Template reset to default.",
    "template.confirm.title": "Reset to Default",
    "template.confirm.save": "Save current template to JSON before resetting?",
    "template.error.noMetadata": "No job metadata available. Run a job to preview.",
    "template.error.invalidField": "Unsupported field(s): {{fields}}",
    "template.error.depotLimit": "Depot count exceeds limit of {{limit}}.",
    "template.error.lengthLimit": "Rendered output exceeds {{limit}} characters.",
    "template.error.noDepots": "No depots available for preview.",
    "template.error.invalidFile": "Invalid template file.",
    "template.error.loadFailed": "Failed to load template: {{error}}",
    "template.error.saveFailed": "Failed to save template: {{error}}",
    "template.preview.metaReady": "Using metadata from the last completed job.",
    "template.preview.metaMissing": "Preview requires a completed job to supply metadata.",
    "template.preview.metaDefault": "Using example metadata for preview. Run a job to use real data.",
    "steamGuard.title": "Steam Guard Confirmation",
    "steamGuard.message": "STEAM GUARD! Use the Steam Mobile App to confirm your sign in...",
    "steamGuard.waiting": "Waiting for confirmation...",
    "steamGuard.email.title": "Steam Guard Email Code",
    "steamGuard.email.status": "Enter the code from your email.",
    "steamGuard.email.empty": "Enter the email code before submitting.",
    "steamGuard.email.sent": "[system] Steam Guard email code submitted.",
    "steamGuard.email.incorrect":
      "[system] Steam Guard email code incorrect. Restarting queue to request a new code.",
    "steamGuard.email.failed": "[system] Steam Guard email code failed: {{error}}",
    "action.start": "Start",
    "action.cancel": "Cancel",
    "action.copy": "Copy",
    "action.close": "Close",
    "action.submit": "Submit",
    "action.yes": "Yes",
    "action.no": "No",
    "language.en": "English",
    "language.es": "Spanish",
    "language.fr": "French",
    "language.ru": "Russian",
    "language.de": "German",
    "os.windowsX64": "Windows x64",
    "os.windowsX86": "Windows x86",
    "os.linux": "Linux",
    "os.macos": "MacOS",
    "os.macosX64": "MacOS",
    "os.macosArm64": "MacOS",
    "alert.clearQueue": "Cannot clear queue while a job is running. Cancel the job first.",
    "dd.noDepots":
      "[system] DepotDownloader reported no depots. Verify the OS dropdown matches the target app (Windows x64 is typical).",
    "job.runningMessage": "A download is already running. Please wait or cancel.",
    "job.tauriUnavailable": "Tauri invoke API unavailable. Unable to start job.",
    "job.noQueued": "No queued jobs.",
    "job.outputDir": "Output directory: {{path}}",
    "job.outputDirError": "Output directory lookup failed: {{error}}",
    "job.selectedOs": "[system] Selected OS: {{os}}",
    "job.starting": "Starting DepotDownloader for AppID {{appId}}...",
    "job.startFailed": "Start failed: {{error}}",
    "job.canceling": "[system] Cancelling job...",
    "job.cancelled": "[system] Job cancelled by user.",
    "job.cancelFailed": "[system] Cancel failed: {{error}}",
    "output.unavailable": "Tauri invoke API unavailable. Unable to open folder.",
    "output.failed": "Failed to open output folder: {{error}}",
    "zip.status": "[system] 7-Zip status: {{status}}",
  },
  es: {
    "tab.queue": "Cola",
    "tab.console": "Consola",
    "queue.label": "Cola:",
    "queue.clear": "Limpiar cola",
    "queue.empty": "No hay descargas en cola.",
    "queue.running": "La cola está en ejecución",
    "queue.noReorder": "No se puede reordenar/eliminar mientras se ejecuta",
    "queue.atTop": "Ya está arriba",
    "queue.atBottom": "Ya está abajo",
    "queue.appId": "AppID {{appId}}",
    "queue.meta": "Rama: {{branch}} • SO: {{os}}",
    "queue.status.queued": "en cola",
    "queue.status.running": "en ejecución",
    "queue.status.compressing": "comprimiendo",
    "queue.status.done": "completado",
    "queue.status.failed": "fallido",
    "auth.username": "Usuario de Steam:",
    "auth.password": "Contraseña de Steam:",
    "auth.qr": "Usar inicio de sesión por QR",
    "auth.save": "Guardar datos de inicio de sesión",
    "auth.saveMissing":
      "Ingrese el usuario y la contraseña de Steam antes de guardar.",
    "auth.saveFailed":
      "No se pudieron guardar los datos de inicio de sesión: {{error}}",
    "auth.deleteFailed":
      "No se pudo eliminar los datos de inicio de sesión guardados: {{error}}",
    "auth.loadFailed":
      "No se pudieron cargar los datos de inicio de sesión guardados: {{error}}",
    "auth.tauriUnavailable": "La API invoke de Tauri no está disponible.",
    "auth.reuseQr": "[system] Reutilizando el inicio de sesión por QR para {{username}}.",
    "game.title": "Gestor de juegos",
    "game.appid": "AppID:",
    "game.add": "Agregar a la cola",
    "game.os": "SO:",
    "branch.title": "Gestor de ramas",
    "branch.label": "Rama para descargar:",
    "branch.password": "¿Contraseña de la rama? (Ingrese abajo)",
    "output.open": "Abrir carpeta de salida",
    "output.conflict.title": "La salida ya existe",
    "output.conflict.message":
      "Ya existe una salida llamada \"{{name}}\".",
    "output.conflict.path": "Ubicación: {{path}}",
    "output.conflict.overwrite": "Sobrescribir",
    "output.conflict.copy": "Crear copia",
    "output.conflict.cancel": "Cancelar",
    "output.conflict.log":
      "[system] La salida ya existe: {{path}}. Elige sobrescribir, copiar o cancelar.",
    "output.conflict.choice.overwrite":
      "[system] Sobrescribiendo la salida existente.",
    "output.conflict.choice.copy":
      "[system] Creando una salida de copia.",
    "output.conflict.choice.cancel":
      "[system] Conflicto de salida cancelado por el usuario.",
    "output.conflict.resolveError":
      "[system] Falló la respuesta del conflicto de salida: {{error}}",
    "console.noSelection": "No hay ninguna tarea seleccionada.",
    "console.noQueuedJobs": "No hay tareas en cola.",
    "qr.title": "Inicio de sesión QR de Steam",
    "qr.help": "Escanee con la app móvil de Steam",
    "qr.waiting": "Esperando la salida del código QR…",
    "settings.button": "Configuración",
    "settings.title": "Configuración",
    "settings.language": "Idioma:",
    "settings.skipCompression": "Omitir compresión después de la descarga",
    "settings.compressionPasswordToggle": "Usar contraseña de compresión",
    "settings.compressionPasswordLabel": "Contraseña de compresión:",
    "settings.compressionPasswordRequired":
      "La contraseña de compresión no puede habilitarse sin establecer una contraseña.",
    "settings.compressionPasswordHint":
      "El cifrado de encabezados (-mhe=on) se activa por defecto con contraseña.",
    "settings.customCompressionArgsLabel": "Argumentos personalizados de 7-Zip:",
    "settings.customCompressionArgsHint":
      "Flags adicionales para 7-Zip. OmniPacker ajusta -mmt y -md automáticamente; modificarlos puede causar problemas. Los flags -t, -p y -bsp no están permitidos.",
    "settings.defaultQrLogin": "Usar QR de forma predeterminada",
    "settings.deleteLogin": "Eliminar datos de inicio de sesión guardados",
    "settings.deleteNotImplemented": "Esta función aún no está implementada.",
    "template.button": "Editor de plantillas",
    "template.title": "Editor de plantillas",
    "template.builder": "Constructor de bloques",
    "template.preview": "Vista previa en vivo",
    "template.addBlock": "Agregar bloque",
    "template.copy": "Copiar BBCode",
    "template.save": "Guardar JSON",
    "template.load": "Cargar JSON",
    "template.reset": "Restablecer a predeterminado",
    "template.block.title": "Bloque de título",
    "template.block.version": "Bloque de versión",
    "template.block.depot_list": "Bloque de lista de depósitos",
    "template.block.free_text": "Bloque de texto libre",
    "template.block.uploaded_version": "Bloque de versión subida",
    "template.field.template": "Plantilla",
    "template.field.text": "Texto",
    "template.field.depotTitle": "Título del spoiler",
    "template.field.depotLine": "Plantilla de línea de depósito",
    "template.field.useCode": "Envolver en [code=text]",
    "template.action.up": "Mover arriba",
    "template.action.down": "Mover abajo",
    "template.action.remove": "Eliminar",
    "template.status.ready": "Plantilla lista.",
    "template.status.loaded": "Plantilla cargada.",
    "template.status.saved": "Plantilla guardada.",
    "template.status.reset": "Plantilla restablecida al valor predeterminado.",
    "template.confirm.title": "Restablecer a predeterminado",
    "template.confirm.save": "¿Guardar la plantilla actual en JSON antes de restablecer?",
    "template.error.noMetadata": "No hay metadatos del trabajo. Ejecute una tarea para previsualizar.",
    "template.error.invalidField": "Campo(s) no compatible(s): {{fields}}",
    "template.error.depotLimit": "La cantidad de depósitos supera el límite de {{limit}}.",
    "template.error.lengthLimit": "La salida generada supera los {{limit}} caracteres.",
    "template.error.noDepots": "No hay depósitos disponibles para la vista previa.",
    "template.error.invalidFile": "Archivo de plantilla no válido.",
    "template.error.loadFailed": "Error al cargar la plantilla: {{error}}",
    "template.error.saveFailed": "Error al guardar la plantilla: {{error}}",
    "template.preview.metaReady": "Usando metadatos del último trabajo completado.",
    "template.preview.metaMissing": "La vista previa requiere un trabajo completado para obtener metadatos.",
    "template.preview.metaDefault": "Usando metadatos de ejemplo para la vista previa. Ejecute una tarea para usar datos reales.",
    "steamGuard.title": "Confirmación de Steam Guard",
    "steamGuard.message":
      "STEAM GUARD! Use la app móvil de Steam para confirmar su inicio de sesión...",
    "steamGuard.waiting": "Esperando confirmación...",
    "steamGuard.email.title": "Steam Guard Email Code",
    "steamGuard.email.status": "Enter the code from your email.",
    "steamGuard.email.empty": "Enter the email code before submitting.",
    "steamGuard.email.sent": "[system] Steam Guard email code submitted.",
    "steamGuard.email.failed": "[system] Steam Guard email code failed: {{error}}",
    "action.start": "Iniciar",
    "action.cancel": "Cancelar",
    "action.copy": "Copiar",
    "action.close": "Cerrar",
    "action.submit": "Submit",
    "action.yes": "Sí",
    "action.no": "No",
    "language.en": "Inglés",
    "language.es": "Español",
    "language.fr": "Francés",
    "language.ru": "Ruso",
    "language.de": "Alemán",
    "os.windowsX64": "Windows x64",
    "os.windowsX86": "Windows x86",
    "os.linux": "Linux",
    "os.macos": "MacOS",
    "os.macosX64": "MacOS",
    "os.macosArm64": "MacOS",
    "alert.clearQueue":
      "No se puede limpiar la cola mientras hay una tarea en ejecución. Cancele la tarea primero.",
    "dd.noDepots":
      "[system] DepotDownloader no encontró depósitos. Verifique que el desplegable de SO coincida con la app (Windows x64 suele ser lo habitual).",
    "job.runningMessage": "Ya hay una descarga en ejecución. Espere o cancele.",
    "job.tauriUnavailable":
      "La API invoke de Tauri no está disponible. No se puede iniciar la tarea.",
    "job.noQueued": "No hay tareas en cola.",
    "job.outputDir": "Directorio de salida: {{path}}",
    "job.outputDirError": "No se pudo obtener el directorio de salida: {{error}}",
    "job.selectedOs": "[system] SO seleccionado: {{os}}",
    "job.starting": "Iniciando DepotDownloader para AppID {{appId}}...",
    "job.startFailed": "El inicio falló: {{error}}",
    "job.canceling": "[system] Cancelando tarea...",
    "job.cancelled": "[system] Tarea cancelada por el usuario.",
    "job.cancelFailed": "[system] Cancelación fallida: {{error}}",
    "output.unavailable":
      "La API invoke de Tauri no está disponible. No se puede abrir la carpeta.",
    "output.failed": "No se pudo abrir la carpeta de salida: {{error}}",
    "zip.status": "[system] Estado de 7-Zip: {{status}}",
  },
  fr: {
    "tab.queue": "File",
    "tab.console": "Console",
    "queue.label": "File:",
    "queue.clear": "Vider la file",
    "queue.empty": "Aucun téléchargement en file.",
    "queue.running": "La file est en cours",
    "queue.noReorder": "Impossible de réorganiser/supprimer pendant l'exécution",
    "queue.atTop": "Déjà en haut",
    "queue.atBottom": "Déjà en bas",
    "queue.appId": "AppID {{appId}}",
    "queue.meta": "Branche : {{branch}} • OS : {{os}}",
    "queue.status.queued": "en file",
    "queue.status.running": "en cours",
    "queue.status.compressing": "compression",
    "queue.status.done": "terminé",
    "queue.status.failed": "échoué",
    "auth.username": "Nom d'utilisateur Steam:",
    "auth.password": "Mot de passe Steam:",
    "auth.qr": "Connexion par QR",
    "auth.save": "Enregistrer les identifiants",
    "auth.saveMissing":
      "Saisissez le nom d'utilisateur et le mot de passe Steam avant d'enregistrer.",
    "auth.saveFailed":
      "Impossible d'enregistrer les identifiants: {{error}}",
    "auth.deleteFailed":
      "Impossible de supprimer les identifiants enregistrés: {{error}}",
    "auth.loadFailed":
      "Impossible de charger les identifiants enregistrés: {{error}}",
    "auth.tauriUnavailable": "L'API invoke de Tauri est indisponible.",
    "auth.reuseQr": "[system] Réutilisation de la connexion QR pour {{username}}.",
    "game.title": "Gestionnaire de jeux",
    "game.appid": "AppID:",
    "game.add": "Ajouter à la file",
    "game.os": "OS:",
    "branch.title": "Gestionnaire de branches",
    "branch.label": "Branche à télécharger:",
    "branch.password": "Mot de passe de branche ? (Saisir ci-dessous)",
    "output.open": "Ouvrir le dossier de sortie",
    "output.conflict.title": "La sortie existe déjà",
    "output.conflict.message":
      "Une sortie nommée \"{{name}}\" existe déjà.",
    "output.conflict.path": "Emplacement : {{path}}",
    "output.conflict.overwrite": "Écraser",
    "output.conflict.copy": "Créer une copie",
    "output.conflict.cancel": "Annuler",
    "output.conflict.log":
      "[system] La sortie existe déjà : {{path}}. Choisissez écraser, copier ou annuler.",
    "output.conflict.choice.overwrite":
      "[system] Écrasement de la sortie existante.",
    "output.conflict.choice.copy":
      "[system] Création d'une copie de la sortie.",
    "output.conflict.choice.cancel":
      "[system] Conflit de sortie annulé par l'utilisateur.",
    "output.conflict.resolveError":
      "[system] Échec de la réponse au conflit de sortie : {{error}}",
    "console.noSelection": "Aucune tâche sélectionnée.",
    "console.noQueuedJobs": "Aucune tâche en file.",
    "qr.title": "Connexion QR Steam",
    "qr.help": "Scannez avec l'application mobile Steam",
    "qr.waiting": "En attente de la sortie du code QR…",
    "settings.button": "Paramètres",
    "settings.title": "Paramètres",
    "settings.language": "Langue:",
    "settings.skipCompression": "Ignorer la compression après le téléchargement",
    "settings.compressionPasswordToggle": "Utiliser un mot de passe de compression",
    "settings.compressionPasswordLabel": "Mot de passe de compression :",
    "settings.compressionPasswordRequired":
      "Le mot de passe de compression ne peut pas être activé sans en définir un.",
    "settings.compressionPasswordHint":
      "Le chiffrement des en-têtes (-mhe=on) est activé par défaut avec un mot de passe.",
    "settings.customCompressionArgsLabel": "Arguments 7-Zip personnalisés :",
    "settings.customCompressionArgsHint":
      "Flags supplémentaires pour 7-Zip. OmniPacker ajuste -mmt et -md automatiquement; les modifier peut causer des problèmes. Les flags -t, -p et -bsp ne sont pas autorisés.",
    "settings.defaultQrLogin": "Utiliser QR par défaut",
    "settings.deleteLogin": "Supprimer les identifiants enregistrés",
    "settings.deleteNotImplemented": "Cette fonctionnalité n'est pas encore implémentée.",
    "template.button": "Éditeur de modèles",
    "template.title": "Éditeur de modèles",
    "template.builder": "Constructeur de blocs",
    "template.preview": "Aperçu en direct",
    "template.addBlock": "Ajouter un bloc",
    "template.copy": "Copier le BBCode",
    "template.save": "Enregistrer le JSON",
    "template.load": "Charger le JSON",
    "template.reset": "Réinitialiser par défaut",
    "template.block.title": "Bloc de titre",
    "template.block.version": "Bloc de version",
    "template.block.depot_list": "Bloc de liste des dépôts",
    "template.block.free_text": "Bloc de texte libre",
    "template.block.uploaded_version": "Bloc de version téléversée",
    "template.field.template": "Modèle",
    "template.field.text": "Texte",
    "template.field.depotTitle": "Titre du spoiler",
    "template.field.depotLine": "Modèle de ligne de dépôt",
    "template.field.useCode": "Encadrer dans [code=text]",
    "template.action.up": "Monter",
    "template.action.down": "Descendre",
    "template.action.remove": "Supprimer",
    "template.status.ready": "Modèle prêt.",
    "template.status.loaded": "Modèle chargé.",
    "template.status.saved": "Modèle enregistré.",
    "template.status.reset": "Modèle réinitialisé par défaut.",
    "template.confirm.title": "Réinitialiser par défaut",
    "template.confirm.save": "Enregistrer le modèle actuel en JSON avant de réinitialiser ?",
    "template.error.noMetadata": "Aucune métadonnée de tâche disponible. Exécutez une tâche pour prévisualiser.",
    "template.error.invalidField": "Champ(s) non pris en charge : {{fields}}",
    "template.error.depotLimit": "Le nombre de dépôts dépasse la limite de {{limit}}.",
    "template.error.lengthLimit": "La sortie générée dépasse {{limit}} caractères.",
    "template.error.noDepots": "Aucun dépôt disponible pour l'aperçu.",
    "template.error.invalidFile": "Fichier de modèle invalide.",
    "template.error.loadFailed": "Échec du chargement du modèle : {{error}}",
    "template.error.saveFailed": "Échec de l'enregistrement du modèle : {{error}}",
    "template.preview.metaReady": "Utilisation des métadonnées de la dernière tâche terminée.",
    "template.preview.metaMissing": "L'aperçu nécessite une tâche terminée pour fournir des métadonnées.",
    "template.preview.metaDefault": "Utilisation de métadonnées d'exemple pour l'aperçu. Exécutez une tâche pour utiliser des données réelles.",
    "steamGuard.title": "Confirmation Steam Guard",
    "steamGuard.message":
      "STEAM GUARD! Utilisez l'application mobile Steam pour confirmer votre connexion...",
    "steamGuard.waiting": "En attente de confirmation...",
    "steamGuard.email.title": "Steam Guard Email Code",
    "steamGuard.email.status": "Enter the code from your email.",
    "steamGuard.email.empty": "Enter the email code before submitting.",
    "steamGuard.email.sent": "[system] Steam Guard email code submitted.",
    "steamGuard.email.failed": "[system] Steam Guard email code failed: {{error}}",
    "action.start": "Démarrer",
    "action.cancel": "Annuler",
    "action.copy": "Copier",
    "action.close": "Fermer",
    "action.submit": "Submit",
    "action.yes": "Oui",
    "action.no": "Non",
    "language.en": "Anglais",
    "language.es": "Espagnol",
    "language.fr": "Français",
    "language.ru": "Russe",
    "language.de": "Allemand",
    "os.windowsX64": "Windows x64",
    "os.windowsX86": "Windows x86",
    "os.linux": "Linux",
    "os.macos": "MacOS",
    "os.macosX64": "MacOS",
    "os.macosArm64": "MacOS",
    "alert.clearQueue":
      "Impossible de vider la file pendant qu'une tâche est en cours. Annulez d'abord la tâche.",
    "dd.noDepots":
      "[system] DepotDownloader n'a trouvé aucun dépôt. Vérifiez que la liste OS correspond à l'application (Windows x64 est généralement le bon choix).",
    "job.runningMessage":
      "Un téléchargement est déjà en cours. Veuillez patienter ou annuler.",
    "job.tauriUnavailable":
      "L'API invoke de Tauri est indisponible. Impossible de démarrer la tâche.",
    "job.noQueued": "Aucune tâche en file.",
    "job.outputDir": "Dossier de sortie: {{path}}",
    "job.outputDirError": "Impossible d'obtenir le dossier de sortie: {{error}}",
    "job.selectedOs": "[system] OS sélectionné: {{os}}",
    "job.starting": "Démarrage de DepotDownloader pour l'AppID {{appId}}...",
    "job.startFailed": "Échec du démarrage: {{error}}",
    "job.canceling": "[system] Annulation de la tâche...",
    "job.cancelled": "[system] Tâche annulée par l'utilisateur.",
    "job.cancelFailed": "[system] Échec de l'annulation: {{error}}",
    "output.unavailable":
      "L'API invoke de Tauri est indisponible. Impossible d'ouvrir le dossier.",
    "output.failed": "Impossible d'ouvrir le dossier de sortie: {{error}}",
    "zip.status": "[system] Statut 7-Zip: {{status}}",
  },
  de: {
    "tab.queue": "Warteschlange",
    "tab.console": "Konsole",
    "queue.label": "Warteschlange:",
    "queue.clear": "Warteschlange leeren",
    "queue.empty": "Keine Downloads in der Warteschlange.",
    "queue.running": "Warteschlange läuft",
    "queue.noReorder": "Reihenfolge/Entfernen während der Ausführung nicht möglich",
    "queue.atTop": "Bereits ganz oben",
    "queue.atBottom": "Bereits ganz unten",
    "queue.appId": "AppID {{appId}}",
    "queue.meta": "Branch: {{branch}} • OS: {{os}}",
    "queue.status.queued": "in Warteschlange",
    "queue.status.running": "läuft",
    "queue.status.compressing": "komprimieren",
    "queue.status.done": "fertig",
    "queue.status.failed": "fehlgeschlagen",
    "auth.username": "Steam-Benutzername:",
    "auth.password": "Steam-Passwort:",
    "auth.qr": "QR-Login verwenden",
    "auth.save": "Login-Daten speichern",
    "auth.saveMissing":
      "Bitte Steam-Benutzername und Passwort eingeben, bevor gespeichert wird.",
    "auth.saveFailed": "Login-Daten konnten nicht gespeichert werden: {{error}}",
    "auth.deleteFailed":
      "Gespeicherte Login-Daten konnten nicht gelöscht werden: {{error}}",
    "auth.loadFailed":
      "Gespeicherte Login-Daten konnten nicht geladen werden: {{error}}",
    "auth.tauriUnavailable": "Tauri invoke API nicht verfügbar.",
    "auth.reuseQr": "[system] QR-Login für {{username}} wird wiederverwendet.",
    "game.title": "Spiel-Manager",
    "game.appid": "AppID:",
    "game.add": "Einreihen",
    "game.os": "OS:",
    "branch.title": "Branch-Manager",
    "branch.label": "Branch zum Download:",
    "branch.password": "Branch-Passwort? (Unten eingeben)",
    "output.open": "Ausgabeordner öffnen",
    "output.conflict.title": "Ausgabe bereits vorhanden",
    "output.conflict.message":
      "Eine Ausgabe namens \"{{name}}\" ist bereits vorhanden.",
    "output.conflict.path": "Speicherort: {{path}}",
    "output.conflict.overwrite": "Überschreiben",
    "output.conflict.copy": "Kopie erstellen",
    "output.conflict.cancel": "Abbrechen",
    "output.conflict.log":
      "[system] Ausgabe bereits vorhanden: {{path}}. Überschreiben, kopieren oder abbrechen.",
    "output.conflict.choice.overwrite":
      "[system] Vorhandene Ausgabe wird überschrieben.",
    "output.conflict.choice.copy":
      "[system] Kopie der Ausgabe wird erstellt.",
    "output.conflict.choice.cancel":
      "[system] Ausgabekonflikt vom Benutzer abgebrochen.",
    "output.conflict.resolveError":
      "[system] Antwort auf Ausgabekonflikt fehlgeschlagen: {{error}}",
    "console.noSelection": "Kein Job ausgewählt.",
    "console.noQueuedJobs": "Keine Jobs in der Warteschlange.",
    "qr.title": "Steam-QR-Login",
    "qr.help": "Mit der Steam-Mobile-App scannen",
    "qr.waiting": "Warte auf QR-Code-Ausgabe…",
    "settings.button": "Einstellungen",
    "settings.title": "Einstellungen",
    "settings.language": "Sprache:",
    "settings.skipCompression": "Komprimierung nach dem Download überspringen",
    "settings.compressionPasswordToggle": "Kompressionspasswort verwenden",
    "settings.compressionPasswordLabel": "Kompressionspasswort:",
    "settings.compressionPasswordRequired":
      "Kompressionspasswort kann nicht aktiviert werden, ohne ein Passwort festzulegen.",
    "settings.compressionPasswordHint":
      "Header-Verschlüsselung (-mhe=on) ist standardmäßig bei Passwort aktiviert.",
    "settings.customCompressionArgsLabel": "Benutzerdefinierte 7-Zip-Argumente:",
    "settings.customCompressionArgsHint":
      "Zusätzliche Flags für 7-Zip. OmniPacker passt -mmt und -md automatisch an; Änderungen können Probleme verursachen. Die Flags -t, -p und -bsp sind nicht erlaubt.",
    "settings.defaultQrLogin": "Standardmäßig QR-Login verwenden",
    "settings.deleteLogin": "Gespeicherte Login-Daten löschen",
    "settings.deleteNotImplemented": "Diese Funktion ist noch nicht implementiert.",
    "template.button": "Vorlageneditor",
    "template.title": "Vorlageneditor",
    "template.builder": "Block-Editor",
    "template.preview": "Live-Vorschau",
    "template.addBlock": "Block hinzufügen",
    "template.copy": "BBCode kopieren",
    "template.save": "JSON speichern",
    "template.load": "JSON laden",
    "template.reset": "Auf Standard zurücksetzen",
    "template.block.title": "Titelblock",
    "template.block.version": "Versionsblock",
    "template.block.depot_list": "Depotlistenblock",
    "template.block.free_text": "Freitextblock",
    "template.block.uploaded_version": "Block für hochgeladene Version",
    "template.field.template": "Vorlage",
    "template.field.text": "Text",
    "template.field.depotTitle": "Spoiler-Titel",
    "template.field.depotLine": "Depotzeilen-Vorlage",
    "template.field.useCode": "In [code=text] einschließen",
    "template.action.up": "Nach oben",
    "template.action.down": "Nach unten",
    "template.action.remove": "Entfernen",
    "template.status.ready": "Vorlage bereit.",
    "template.status.loaded": "Vorlage geladen.",
    "template.status.saved": "Vorlage gespeichert.",
    "template.status.reset": "Vorlage auf Standard zurückgesetzt.",
    "template.confirm.title": "Auf Standard zurücksetzen",
    "template.confirm.save": "Aktuelle Vorlage vor dem Zurücksetzen als JSON speichern?",
    "template.error.noMetadata": "Keine Job-Metadaten verfügbar. Starte einen Job für die Vorschau.",
    "template.error.invalidField": "Nicht unterstützte Felder: {{fields}}",
    "template.error.depotLimit": "Anzahl der Depots überschreitet das Limit von {{limit}}.",
    "template.error.lengthLimit": "Die Ausgabe überschreitet {{limit}} Zeichen.",
    "template.error.noDepots": "Keine Depots für die Vorschau verfügbar.",
    "template.error.invalidFile": "Ungültige Vorlagendatei.",
    "template.error.loadFailed": "Vorlage konnte nicht geladen werden: {{error}}",
    "template.error.saveFailed": "Vorlage konnte nicht gespeichert werden: {{error}}",
    "template.preview.metaReady": "Verwendet Metadaten des zuletzt abgeschlossenen Jobs.",
    "template.preview.metaMissing": "Für die Vorschau wird ein abgeschlossener Job benötigt.",
    "template.preview.metaDefault": "Verwendet Beispielmetadaten für die Vorschau. Starte einen Job für echte Daten.",
    "steamGuard.title": "Steam-Guard-Bestätigung",
    "steamGuard.message":
      "STEAM GUARD! Verwenden Sie die Steam-Mobile-App, um Ihren Login zu bestätigen...",
    "steamGuard.waiting": "Warten auf Bestätigung...",
    "steamGuard.email.title": "Steam Guard Email Code",
    "steamGuard.email.status": "Enter the code from your email.",
    "steamGuard.email.empty": "Enter the email code before submitting.",
    "steamGuard.email.sent": "[system] Steam Guard email code submitted.",
    "steamGuard.email.failed": "[system] Steam Guard email code failed: {{error}}",
    "action.start": "Start",
    "action.cancel": "Abbrechen",
    "action.copy": "Kopieren",
    "action.close": "Schließen",
    "action.submit": "Submit",
    "action.yes": "Ja",
    "action.no": "Nein",
    "language.en": "Englisch",
    "language.es": "Spanisch",
    "language.fr": "Französisch",
    "language.ru": "Russisch",
    "language.de": "Deutsch",
    "os.windowsX64": "Windows x64",
    "os.windowsX86": "Windows x86",
    "os.linux": "Linux",
    "os.macos": "MacOS",
    "os.macosX64": "MacOS",
    "os.macosArm64": "MacOS",
    "alert.clearQueue":
      "Warteschlange kann nicht geleert werden, während ein Job läuft. Brechen Sie den Job zuerst ab.",
    "dd.noDepots":
      "[system] DepotDownloader hat keine Depots gefunden. Stellen Sie sicher, dass das OS-Dropdown zur App passt (Windows x64 ist üblich).",
    "job.runningMessage": "Ein Download läuft bereits. Bitte warten oder abbrechen.",
    "job.tauriUnavailable":
      "Tauri invoke API nicht verfügbar. Job kann nicht gestartet werden.",
    "job.noQueued": "Keine Jobs in der Warteschlange.",
    "job.outputDir": "Ausgabeverzeichnis: {{path}}",
    "job.outputDirError":
      "Ausgabeverzeichnis konnte nicht ermittelt werden: {{error}}",
    "job.selectedOs": "[system] Ausgewähltes OS: {{os}}",
    "job.starting": "DepotDownloader wird für AppID {{appId}} gestartet...",
    "job.startFailed": "Start fehlgeschlagen: {{error}}",
    "job.canceling": "[system] Job wird abgebrochen...",
    "job.cancelled": "[system] Job vom Benutzer abgebrochen.",
    "job.cancelFailed": "[system] Abbruch fehlgeschlagen: {{error}}",
    "output.unavailable":
      "Tauri invoke API nicht verfügbar. Ordner kann nicht geöffnet werden.",
    "output.failed": "Ausgabeordner konnte nicht geöffnet werden: {{error}}",
    "zip.status": "[system] 7-Zip-Status: {{status}}",
  },
  ru: {
    "tab.queue": "Очередь",
    "tab.console": "Консоль",
    "queue.label": "Очередь:",
    "queue.clear": "Очистить очередь",
    "queue.empty": "Нет загрузок в очереди.",
    "queue.running": "Очередь выполняется",
    "queue.noReorder": "Нельзя менять порядок/удалять во время выполнения",
    "queue.atTop": "Уже вверху",
    "queue.atBottom": "Уже внизу",
    "queue.appId": "AppID {{appId}}",
    "queue.meta": "Ветка: {{branch}} • ОС: {{os}}",
    "queue.status.queued": "в очереди",
    "queue.status.running": "выполняется",
    "queue.status.compressing": "сжатие",
    "queue.status.done": "готово",
    "queue.status.failed": "ошибка",
    "auth.username": "Имя пользователя Steam:",
    "auth.password": "Пароль Steam:",
    "auth.qr": "Вход по QR",
    "auth.save": "Сохранить данные входа",
    "auth.saveMissing":
      "Введите имя пользователя и пароль Steam перед сохранением.",
    "auth.saveFailed": "Не удалось сохранить данные входа: {{error}}",
    "auth.deleteFailed": "Не удалось удалить сохраненные данные: {{error}}",
    "auth.loadFailed": "Не удалось загрузить сохраненные данные: {{error}}",
    "auth.tauriUnavailable": "Tauri invoke API недоступен.",
    "auth.reuseQr": "[system] Повторный вход по QR для {{username}}.",
    "game.title": "Менеджер игр",
    "game.appid": "AppID:",
    "game.add": "Добавить в очередь",
    "game.os": "ОС:",
    "branch.title": "Менеджер веток",
    "branch.label": "Ветка для загрузки:",
    "branch.password": "Пароль ветки? (Введите ниже)",
    "output.open": "Открыть папку вывода",
    "output.conflict.title": "Вывод уже существует",
    "output.conflict.message":
      "Вывод с именем \"{{name}}\" уже существует.",
    "output.conflict.path": "Расположение: {{path}}",
    "output.conflict.overwrite": "Перезаписать",
    "output.conflict.copy": "Создать копию",
    "output.conflict.cancel": "Отмена",
    "output.conflict.log":
      "[system] Вывод уже существует: {{path}}. Выберите перезаписать, копировать или отменить.",
    "output.conflict.choice.overwrite":
      "[system] Перезапись существующего вывода.",
    "output.conflict.choice.copy":
      "[system] Создание копии вывода.",
    "output.conflict.choice.cancel":
      "[system] Конфликт вывода отменен пользователем.",
    "output.conflict.resolveError":
      "[system] Не удалось отправить ответ по конфликту вывода: {{error}}",
    "console.noSelection": "Задание не выбрано.",
    "console.noQueuedJobs": "В очереди нет заданий.",
    "qr.title": "Вход по QR Steam",
    "qr.help": "Сканируйте в мобильном приложении Steam",
    "qr.waiting": "Ожидание вывода QR-кода…",
    "settings.button": "Настройки",
    "settings.title": "Настройки",
    "settings.language": "Язык:",
    "settings.skipCompression": "Пропустить сжатие после загрузки",
    "settings.compressionPasswordToggle": "Использовать пароль для сжатия",
    "settings.compressionPasswordLabel": "Пароль для сжатия:",
    "settings.compressionPasswordRequired":
      "Нельзя включить пароль сжатия без заданного пароля.",
    "settings.compressionPasswordHint":
      "Шифрование заголовков (-mhe=on) включено по умолчанию при наличии пароля.",
    "settings.customCompressionArgsLabel": "Пользовательские аргументы 7-Zip:",
    "settings.customCompressionArgsHint":
      "Дополнительные флаги для 7-Zip. OmniPacker автоматически настраивает -mmt и -md; их изменение может вызвать проблемы. Флаги -t, -p и -bsp запрещены.",
    "settings.defaultQrLogin": "QR-вход по умолчанию",
    "settings.deleteLogin": "Удалить сохраненные данные входа",
    "settings.deleteNotImplemented": "Эта функция еще не реализована.",
    "template.button": "Редактор шаблонов",
    "template.title": "Редактор шаблонов",
    "template.builder": "Конструктор блоков",
    "template.preview": "Предпросмотр в реальном времени",
    "template.addBlock": "Добавить блок",
    "template.copy": "Копировать BBCode",
    "template.save": "Сохранить JSON",
    "template.load": "Загрузить JSON",
    "template.reset": "Сбросить по умолчанию",
    "template.block.title": "Блок заголовка",
    "template.block.version": "Блок версии",
    "template.block.depot_list": "Блок списка депо",
    "template.block.free_text": "Блок свободного текста",
    "template.block.uploaded_version": "Блок загруженной версии",
    "template.field.template": "Шаблон",
    "template.field.text": "Текст",
    "template.field.depotTitle": "Заголовок спойлера",
    "template.field.depotLine": "Шаблон строки депо",
    "template.field.useCode": "Обернуть в [code=text]",
    "template.action.up": "Вверх",
    "template.action.down": "Вниз",
    "template.action.remove": "Удалить",
    "template.status.ready": "Шаблон готов.",
    "template.status.loaded": "Шаблон загружен.",
    "template.status.saved": "Шаблон сохранен.",
    "template.status.reset": "Шаблон сброшен к настройкам по умолчанию.",
    "template.confirm.title": "Сбросить по умолчанию",
    "template.confirm.save": "Сохранить текущий шаблон в JSON перед сбросом?",
    "template.error.noMetadata": "Нет метаданных задания. Запустите задание для предпросмотра.",
    "template.error.invalidField": "Неподдерживаемые поля: {{fields}}",
    "template.error.depotLimit": "Количество депо превышает лимит {{limit}}.",
    "template.error.lengthLimit": "Сгенерированный вывод превышает {{limit}} символов.",
    "template.error.noDepots": "Нет доступных депо для предпросмотра.",
    "template.error.invalidFile": "Некорректный файл шаблона.",
    "template.error.loadFailed": "Не удалось загрузить шаблон: {{error}}",
    "template.error.saveFailed": "Не удалось сохранить шаблон: {{error}}",
    "template.preview.metaReady": "Используются метаданные последнего завершенного задания.",
    "template.preview.metaMissing": "Для предпросмотра требуется завершенное задание.",
    "template.preview.metaDefault": "Используются примерные метаданные для предпросмотра. Запустите задание, чтобы использовать реальные данные.",
    "steamGuard.title": "Подтверждение Steam Guard",
    "steamGuard.message":
      "STEAM GUARD! Используйте мобильное приложение Steam, чтобы подтвердить вход...",
    "steamGuard.waiting": "Ожидание подтверждения...",
    "steamGuard.email.title": "Steam Guard Email Code",
    "steamGuard.email.status": "Enter the code from your email.",
    "steamGuard.email.empty": "Enter the email code before submitting.",
    "steamGuard.email.sent": "[system] Steam Guard email code submitted.",
    "steamGuard.email.failed": "[system] Steam Guard email code failed: {{error}}",
    "action.start": "Старт",
    "action.cancel": "Отмена",
    "action.copy": "Копировать",
    "action.close": "Закрыть",
    "action.submit": "Submit",
    "action.yes": "Да",
    "action.no": "Нет",
    "language.en": "Английский",
    "language.es": "Испанский",
    "language.fr": "Французский",
    "language.ru": "Русский",
    "language.de": "Немецкий",
    "os.windowsX64": "Windows x64",
    "os.windowsX86": "Windows x86",
    "os.linux": "Linux",
    "os.macos": "MacOS",
    "os.macosX64": "MacOS",
    "os.macosArm64": "MacOS",
    "alert.clearQueue":
      "Нельзя очистить очередь во время выполнения. Сначала отмените задание.",
    "dd.noDepots":
      "[system] DepotDownloader не нашел депоты. Проверьте, что выбранная ОС соответствует приложению (обычно Windows x64).",
    "job.runningMessage": "Загрузка уже выполняется. Подождите или отмените.",
    "job.tauriUnavailable": "Tauri invoke API недоступен. Невозможно запустить задачу.",
    "job.noQueued": "В очереди нет заданий.",
    "job.outputDir": "Каталог вывода: {{path}}",
    "job.outputDirError": "Не удалось определить каталог вывода: {{error}}",
    "job.selectedOs": "[system] Выбранная ОС: {{os}}",
    "job.starting": "Запуск DepotDownloader для AppID {{appId}}...",
    "job.startFailed": "Запуск не удался: {{error}}",
    "job.canceling": "[system] Отмена задания...",
    "job.cancelled": "[system] Задание отменено пользователем.",
    "job.cancelFailed": "[system] Не удалось отменить: {{error}}",
    "output.unavailable": "Tauri invoke API недоступен. Невозможно открыть папку.",
    "output.failed": "Не удалось открыть папку вывода: {{error}}",
    "zip.status": "[system] Статус 7-Zip: {{status}}",
  },
};

const osLabelKeys = {
  "Windows x64": "os.windowsX64",
  "Windows x86": "os.windowsX86",
  Linux: "os.linux",
  macOS: "os.macos",
  "macOS x64": "os.macos",
  "macOS arm64": "os.macos",
};

const qrWaitingTextValues = Object.values(translations)
  .map((entry) => entry["qr.waiting"])
  .filter(Boolean);

const t = (key, vars = {}) => {
  const table = translations[settingsState.language] ?? translations.en;
  const template = table[key] ?? translations.en[key];
  if (!template) {
    return key;
  }
  return template.replace(/\{\{(\w+)\}\}/g, (_, name) => {
    if (Object.prototype.hasOwnProperty.call(vars, name)) {
      return String(vars[name]);
    }
    return "";
  });
};

const formatStatus = (status) => {
  const translated = t(`queue.status.${status}`);
  return translated || status;
};

const formatOsLabel = (osValue) => {
  const key = osLabelKeys[osValue];
  return key ? t(key) : osValue;
};

const TEMPLATE_MAX_DEPOTS = 100;
const TEMPLATE_MAX_LENGTH = 200000;
const TEMPLATE_SINGLE_FIELDS = [
  "game_name",
  "os",
  "branch",
  "build_datetime_utc",
  "build_id",
];
const TEMPLATE_DEPOT_FIELDS = ["depot_id", "depot_name", "manifest_id"];

// Default test metadata for template preview when no job has been run
const TEMPLATE_DEFAULT_METADATA = {
  game_name: "Balatro",
  os: "Win64",
  branch: "Public",
  build_datetime_utc: "February 24, 2025 - 22:02:36 UTC",
  build_id: "4851806656204679952",
  depots: [
    {
      depot_id: "228989",
      depot_name: "Steamworks Shared",
      manifest_id: "7206221393165260579",
    },
    {
      depot_id: "2379781",
      depot_name: "Balatro",
      manifest_id: "4851806656204679952",
    },
  ],
};

const TEMPLATE_BLOCK_TYPES = [
  { type: "title", labelKey: "template.block.title" },
  { type: "version", labelKey: "template.block.version" },
  { type: "depot_list", labelKey: "template.block.depot_list" },
  { type: "free_text", labelKey: "template.block.free_text" },
  { type: "uploaded_version", labelKey: "template.block.uploaded_version" },
  { type: "patch_notes", labelKey: "template.block.patch_notes" },
];

const TEMPLATE_DEFAULTS = {
  title: {
    template:
      "[url=][color=white][b]{{game_name}} [{{os}}] [Branch: {{branch}}] (Clean Steam Files)[/b][/color][/url]",
  },
  version: {
    template:
      "[size=85][color=white][b]Version:[/b] [i]{{build_datetime_utc}} [Build {{build_id}}][/i][/color][/size]",
  },
  depot_list: {
    title: "\"[color=white]Depots & Manifests[/color]\"",
    lineTemplate: "{{depot_id}} - {{depot_name}} [Manifest {{manifest_id}}]",
    useCodeBlock: true,
  },
  free_text: {
    text: "",
  },
  patch_notes: {
    text: "",
  },
  uploaded_version: {
    template:
      "[color=white][b]Uploaded version:[/b] [i]{{build_datetime_utc}} [Build {{build_id}}][/i][/color]",
  },
};

const templateState = {
  blocks: [],
  metadata: null,
};

let templateShowRendered = false;

let templateBlockSequence = 0;

const createTemplateBlockId = () => {
  if (typeof crypto !== "undefined" && crypto.randomUUID) {
    return crypto.randomUUID();
  }
  templateBlockSequence += 1;
  return `template-block-${Date.now()}-${templateBlockSequence}`;
};

const createTemplateBlock = (type) => ({
  id: createTemplateBlockId(),
  type,
  config: { ...(TEMPLATE_DEFAULTS[type] || {}) },
});

const createDefaultTemplate = () => [
  createTemplateBlock("title"),
  createTemplateBlock("version"),
  createTemplateBlock("depot_list"),
  createTemplateBlock("uploaded_version"),
  {
    ...createTemplateBlock("free_text"),
    config: {
      text: "Made using [url=https://github.com/elgreams/OmniPacker]OmniPacker[/url]",
    },
  },
];

const loadDefaultTemplateFromSettings = () => {
  if (!settingsState.defaultTemplate) {
    return null;
  }
  try {
    return parseTemplatePayload(settingsState.defaultTemplate);
  } catch (error) {
    settingsState.defaultTemplate = null;
    saveSettings();
    return null;
  }
};

const persistTemplateDefault = async () => {
  if (templateState.blocks.length === 0) {
    settingsState.defaultTemplate = null;
  } else {
    settingsState.defaultTemplate = serializeTemplate();

    // Also save to Rust backend storage for template file generation
    if (tauriInvoke) {
      try {
        await tauriInvoke("save_template_data", {
          templatePayload: settingsState.defaultTemplate,
        });
      } catch (error) {
        console.debug("[OmniPacker] Failed to save template to backend:", error);
      }
    }
  }
  saveSettings();
};

const setTemplateStatus = (message) => {
  if (templateStatus) {
    templateStatus.textContent = message || "";
  }
};

const syncTemplatePreviewMeta = () => {
  if (!templatePreviewMeta) {
    return;
  }
  if (templateState.metadata) {
    templatePreviewMeta.textContent = t("template.preview.metaReady");
  } else {
    templatePreviewMeta.textContent = t("template.preview.metaDefault");
  }
};

const populateTemplateBlockSelect = () => {
  if (!templateBlockSelect) {
    return;
  }
  const currentValue = templateBlockSelect.value;
  templateBlockSelect.innerHTML = "";
  TEMPLATE_BLOCK_TYPES.forEach((blockType) => {
    const option = document.createElement("option");
    option.value = blockType.type;
    option.textContent = t(blockType.labelKey);
    templateBlockSelect.appendChild(option);
  });
  if (currentValue) {
    templateBlockSelect.value = currentValue;
  }
};

const loadTemplateMetadata = async () => {
  if (!tauriInvoke) {
    templateState.metadata = null;
    syncTemplatePreviewMeta();
    return;
  }
  try {
    const metadata = await tauriInvoke("get_template_metadata");
    templateState.metadata = metadata || null;
  } catch (error) {
    templateState.metadata = null;
  }
  syncTemplatePreviewMeta();
};

const openTemplateEditor = async () => {
  templateModalOverlay?.classList.add("active");
  await syncTemplateStorage();
  if (templateState.blocks.length === 0) {
    const storedBlocks = loadDefaultTemplateFromSettings();
    templateState.blocks = storedBlocks || createDefaultTemplate();

    // Sync template to backend storage on first load
    await persistTemplateDefault();
  }
  populateTemplateBlockSelect();
  setTemplateStatus(t("template.status.ready"));
  await loadTemplateMetadata();
  renderTemplateBuilder();
  renderTemplatePreview();
};

const closeTemplateEditor = () => {
  templateModalOverlay?.classList.remove("active");
};

const moveTemplateBlock = (index, direction) => {
  const nextIndex = index + direction;
  if (
    nextIndex < 0 ||
    nextIndex >= templateState.blocks.length ||
    index < 0
  ) {
    return;
  }
  const [block] = templateState.blocks.splice(index, 1);
  templateState.blocks.splice(nextIndex, 0, block);
  persistTemplateDefault();
  renderTemplateBuilder();
  renderTemplatePreview();
};

const removeTemplateBlock = (index) => {
  if (index < 0 || index >= templateState.blocks.length) {
    return;
  }
  templateState.blocks.splice(index, 1);
  persistTemplateDefault();
  renderTemplateBuilder();
  renderTemplatePreview();
};

const updateTemplateBlockConfig = (blockId, updates) => {
  const target = templateState.blocks.find((block) => block.id === blockId);
  if (!target) {
    return;
  }
  target.config = { ...target.config, ...updates };
  persistTemplateDefault();
  renderTemplatePreview();
};

const addTemplateBlock = (type) => {
  const selected = TEMPLATE_BLOCK_TYPES.find((block) => block.type === type);
  if (!selected) {
    return;
  }
  templateState.blocks.push(createTemplateBlock(type));
  persistTemplateDefault();
  renderTemplateBuilder();
  renderTemplatePreview();
};

const renderTemplateBuilder = () => {
  if (!templateBlocksContainer) {
    return;
  }
  templateBlocksContainer.innerHTML = "";

  templateState.blocks.forEach((block, index) => {
    const blockEl = document.createElement("div");
    blockEl.className = "template-block";

    const header = document.createElement("div");
    header.className = "template-block-header";

    const title = document.createElement("div");
    title.textContent = t(`template.block.${block.type}`);

    const actions = document.createElement("div");
    actions.className = "template-block-actions";

    const upButton = document.createElement("button");
    upButton.type = "button";
    upButton.textContent = "Up";
    upButton.title = t("template.action.up");
    upButton.disabled = index === 0;
    upButton.addEventListener("click", () => moveTemplateBlock(index, -1));

    const downButton = document.createElement("button");
    downButton.type = "button";
    downButton.textContent = "Down";
    downButton.title = t("template.action.down");
    downButton.disabled = index === templateState.blocks.length - 1;
    downButton.addEventListener("click", () => moveTemplateBlock(index, 1));

    const removeButton = document.createElement("button");
    removeButton.type = "button";
    removeButton.textContent = "X";
    removeButton.title = t("template.action.remove");
    removeButton.addEventListener("click", () => removeTemplateBlock(index));

    actions.appendChild(upButton);
    actions.appendChild(downButton);
    actions.appendChild(removeButton);

    header.appendChild(title);
    header.appendChild(actions);
    blockEl.appendChild(header);

    if (block.type === "title" || block.type === "version" || block.type === "uploaded_version") {
      const field = document.createElement("div");
      field.className = "template-block-field";
      const label = document.createElement("label");
      label.textContent = t("template.field.template");
      const input = document.createElement("input");
      input.type = "text";
      input.value = block.config.template || "";
      input.addEventListener("input", () =>
        updateTemplateBlockConfig(block.id, { template: input.value })
      );
      field.appendChild(label);
      field.appendChild(input);
      blockEl.appendChild(field);
    }

    if (block.type === "free_text") {
      const field = document.createElement("div");
      field.className = "template-block-field";
      const label = document.createElement("label");
      label.textContent = t("template.field.text");
      const textarea = document.createElement("textarea");
      textarea.value = block.config.text || "";
      textarea.addEventListener("input", () =>
        updateTemplateBlockConfig(block.id, { text: textarea.value })
      );
      field.appendChild(label);
      field.appendChild(textarea);
      blockEl.appendChild(field);
    }

    if (block.type === "patch_notes") {
      // Fetch + pick a patch note by AppID, then fill the editable content.
      const pickField = document.createElement("div");
      pickField.className = "template-block-field template-patch-pick";
      const appidInput = document.createElement("input");
      appidInput.type = "text";
      appidInput.placeholder = "AppID";
      appidInput.className = "template-patch-appid";
      const fetchBtn = document.createElement("button");
      fetchBtn.type = "button";
      fetchBtn.textContent = "Fetch notes";
      const select = document.createElement("select");
      select.className = "template-patch-select";
      select.style.display = "none";
      pickField.appendChild(appidInput);
      pickField.appendChild(fetchBtn);
      pickField.appendChild(select);
      blockEl.appendChild(pickField);

      const field = document.createElement("div");
      field.className = "template-block-field";
      const label = document.createElement("label");
      label.textContent = t("template.field.text");
      const textarea = document.createElement("textarea");
      textarea.value = block.config.text || "";
      textarea.addEventListener("input", () =>
        updateTemplateBlockConfig(block.id, { text: textarea.value }),
      );
      field.appendChild(label);
      field.appendChild(textarea);
      blockEl.appendChild(field);

      let fetchedItems = [];
      fetchBtn.addEventListener("click", async () => {
        const appid = Number(appidInput.value.trim());
        if (!appid || !tauriInvoke) return;
        fetchBtn.disabled = true;
        fetchBtn.textContent = "…";
        try {
          const res = await tauriInvoke("get_app_news", { appid, count: 15 });
          fetchedItems = res?.items || [];
          select.innerHTML = "";
          const def = document.createElement("option");
          def.value = "";
          def.textContent = `Select a note (${fetchedItems.length})`;
          select.appendChild(def);
          fetchedItems.forEach((item, i) => {
            const o = document.createElement("option");
            o.value = String(i);
            o.textContent = item.title || "(untitled)";
            select.appendChild(o);
          });
          select.style.display = fetchedItems.length ? "" : "none";
        } catch (error) {
          setTemplateStatus(`Patch notes fetch failed: ${error}`);
        } finally {
          fetchBtn.disabled = false;
          fetchBtn.textContent = "Fetch notes";
        }
      });
      select.addEventListener("change", () => {
        const idx = Number(select.value);
        if (Number.isInteger(idx) && fetchedItems[idx]) {
          textarea.value = fetchedItems[idx].contents || "";
          updateTemplateBlockConfig(block.id, { text: textarea.value });
        }
      });
    }

    if (block.type === "depot_list") {
      const titleField = document.createElement("div");
      titleField.className = "template-block-field";
      const titleLabel = document.createElement("label");
      titleLabel.textContent = t("template.field.depotTitle");
      const titleInput = document.createElement("input");
      titleInput.type = "text";
      titleInput.value = block.config.title || "";
      titleInput.addEventListener("input", () =>
        updateTemplateBlockConfig(block.id, { title: titleInput.value })
      );
      titleField.appendChild(titleLabel);
      titleField.appendChild(titleInput);
      blockEl.appendChild(titleField);

      const lineField = document.createElement("div");
      lineField.className = "template-block-field";
      const lineLabel = document.createElement("label");
      lineLabel.textContent = t("template.field.depotLine");
      const lineInput = document.createElement("input");
      lineInput.type = "text";
      lineInput.value = block.config.lineTemplate || "";
      lineInput.addEventListener("input", () =>
        updateTemplateBlockConfig(block.id, { lineTemplate: lineInput.value })
      );
      lineField.appendChild(lineLabel);
      lineField.appendChild(lineInput);
      blockEl.appendChild(lineField);

      const codeRow = document.createElement("label");
      codeRow.className = "checkbox-row";
      const codeToggle = document.createElement("input");
      codeToggle.type = "checkbox";
      codeToggle.checked = Boolean(block.config.useCodeBlock);
      codeToggle.addEventListener("change", () =>
        updateTemplateBlockConfig(block.id, { useCodeBlock: codeToggle.checked })
      );
      const codeLabel = document.createElement("span");
      codeLabel.textContent = t("template.field.useCode");
      codeRow.appendChild(codeToggle);
      codeRow.appendChild(codeLabel);
      blockEl.appendChild(codeRow);
    }

    templateBlocksContainer.appendChild(blockEl);
  });
};

const renderTemplateString = (template, allowedFields, values) => {
  const source = String(template ?? "");
  const tokenRegex = /\{\{([^}]+)\}\}/g;
  const tokens = [];
  let match = tokenRegex.exec(source);
  while (match) {
    tokens.push(match[1].trim());
    match = tokenRegex.exec(source);
  }

  const invalid = tokens.filter((token) => !allowedFields.includes(token));
  if (invalid.length) {
    const unique = [...new Set(invalid)];
    return { error: t("template.error.invalidField", { fields: unique.join(", ") }) };
  }

  const output = source.replace(tokenRegex, (_, token) => {
    const key = token.trim();
    if (Object.prototype.hasOwnProperty.call(values, key)) {
      return String(values[key] ?? "");
    }
    return "";
  });

  return { output };
};

const renderTemplateOutput = (blocks, metadata) => {
  if (!metadata) {
    return { error: t("template.error.noMetadata") };
  }

  const baseValues = {
    game_name: metadata.game_name || "",
    os: metadata.os || "",
    branch: metadata.branch || "",
    build_datetime_utc: metadata.build_datetime_utc || "",
    build_id: metadata.build_id || "",
  };
  const depots = Array.isArray(metadata.depots) ? metadata.depots : [];
  const outputParts = [];

  for (const block of blocks) {
    if (block.type === "title" || block.type === "version" || block.type === "uploaded_version") {
      const template = block.config.template || "";
      const rendered = renderTemplateString(template, TEMPLATE_SINGLE_FIELDS, baseValues);
      if (rendered.error) {
        return rendered;
      }
      outputParts.push(rendered.output);
      continue;
    }

    if (block.type === "free_text") {
      const template = block.config.text || "";
      const rendered = renderTemplateString(template, TEMPLATE_SINGLE_FIELDS, baseValues);
      if (rendered.error) {
        return rendered;
      }
      outputParts.push(rendered.output);
      continue;
    }

    if (block.type === "patch_notes") {
      // Emitted verbatim — patch-note BBCode may contain `{` that isn't a token.
      outputParts.push(block.config.text || "");
      continue;
    }

    if (block.type === "depot_list") {
      if (depots.length === 0) {
        return { error: t("template.error.noDepots") };
      }
      if (depots.length > TEMPLATE_MAX_DEPOTS) {
        return { error: t("template.error.depotLimit", { limit: TEMPLATE_MAX_DEPOTS }) };
      }

      const lineTemplate = block.config.lineTemplate || "";
      const lines = [];
      for (const depot of depots) {
        const depotValues = {
          depot_id: depot.depot_id || "",
          depot_name: depot.depot_name || "",
          manifest_id: depot.manifest_id || "",
        };
        const rendered = renderTemplateString(
          lineTemplate,
          TEMPLATE_DEPOT_FIELDS,
          depotValues
        );
        if (rendered.error) {
          return rendered;
        }
        lines.push(rendered.output);
      }

      const title = block.config.title || "Depots";
      const useCode = Boolean(block.config.useCodeBlock);
      let depotOutput = `[spoiler=${title}]\n`;
      if (useCode) {
        depotOutput += "[code=text]";
      }
      depotOutput += lines.join("\n");
      if (useCode) {
        depotOutput += "[/code]";
      }
      depotOutput += "\n[/spoiler]";
      outputParts.push(depotOutput);
    }
  }

  let output = "";
  for (let i = 0; i < outputParts.length; i += 1) {
    const current = outputParts[i];
    const next = outputParts[i + 1];
    const currentType = blocks[i]?.type;
    const nextType = blocks[i + 1]?.type;
    output += current;
    if (next !== undefined) {
      // Match CS.RIN-style spacing between default blocks.
      let separator = "\n";
      if (currentType === "version" && nextType === "depot_list") {
        separator = "\n\n";
      } else if (currentType === "depot_list" && nextType === "uploaded_version") {
        separator = "";
      }
      output += separator;
    }
  }
  if (output.length > TEMPLATE_MAX_LENGTH) {
    return { error: t("template.error.lengthLimit", { limit: TEMPLATE_MAX_LENGTH }) };
  }

  return { output };
};

const renderTemplatePreview = () => {
  if (!templatePreviewOutput) {
    return;
  }

  // Use fallback test metadata if no job metadata is available
  const metadata = templateState.metadata || TEMPLATE_DEFAULT_METADATA;

  const result = renderTemplateOutput(templateState.blocks, metadata);
  if (result.error) {
    templatePreviewOutput.value = result.error;
    templatePreviewOutput.classList.add("is-error");
    if (templateCopyButton) {
      templateCopyButton.disabled = true;
    }
  } else {
    templatePreviewOutput.value = result.output;
    templatePreviewOutput.classList.remove("is-error");
    if (templateCopyButton) {
      templateCopyButton.disabled = false;
    }
  }

  // Rendered BBCode view (only when toggled on and there's valid output).
  if (templatePreviewRendered) {
    const showRendered = templateShowRendered && !result.error;
    if (showRendered) {
      templatePreviewRendered.innerHTML = bbcodeToHtml(result.output);
      templatePreviewRendered.hidden = false;
      templatePreviewOutput.style.display = "none";
    } else {
      templatePreviewRendered.hidden = true;
      templatePreviewOutput.style.display = "";
    }
  }
};

const serializeTemplateBlocks = (blocks) => ({
  version: 1,
  blocks: blocks.map((block) => ({
    type: block.type,
    config: block.config,
  })),
});

const serializeTemplate = () => serializeTemplateBlocks(templateState.blocks);

const parseTemplatePayload = (payload) => {
  if (!payload || !Array.isArray(payload.blocks)) {
    throw new Error(t("template.error.invalidFile"));
  }
  const parsedBlocks = payload.blocks.map((block) => {
    if (!block || typeof block.type !== "string") {
      throw new Error(t("template.error.invalidFile"));
    }
    if (!TEMPLATE_DEFAULTS[block.type]) {
      throw new Error(t("template.error.invalidFile"));
    }
    const config = block.config && typeof block.config === "object" ? block.config : {};
    let sanitized = {};
    if (block.type === "title" || block.type === "version" || block.type === "uploaded_version") {
      sanitized = {
        template:
          typeof config.template === "string"
            ? config.template
            : TEMPLATE_DEFAULTS[block.type].template,
      };
    } else if (block.type === "free_text") {
      sanitized = {
        text:
          typeof config.text === "string"
            ? config.text
            : TEMPLATE_DEFAULTS.free_text.text,
      };
    } else if (block.type === "depot_list") {
      sanitized = {
        title:
          typeof config.title === "string"
            ? config.title
            : TEMPLATE_DEFAULTS.depot_list.title,
        lineTemplate:
          typeof config.lineTemplate === "string"
            ? config.lineTemplate
            : TEMPLATE_DEFAULTS.depot_list.lineTemplate,
        useCodeBlock:
          typeof config.useCodeBlock === "boolean"
            ? config.useCodeBlock
            : TEMPLATE_DEFAULTS.depot_list.useCodeBlock,
      };
    }
    return {
      id: createTemplateBlockId(),
      type: block.type,
      config: sanitized,
    };
  });
  if (parsedBlocks.length === 0) {
    throw new Error(t("template.error.invalidFile"));
  }
  return parsedBlocks;
};

const isValidTemplatePayload = (payload) => {
  if (!payload || typeof payload !== "object") {
    return false;
  }
  try {
    parseTemplatePayload(payload);
    return true;
  } catch {
    return false;
  }
};

const saveTemplateToFile = async () => {
  const tauriDialog = window.__TAURI__?.dialog;
  const tauriFsWriteText = window.__TAURI__?.fs?.writeTextFile;

  if (!tauriDialog?.save || !tauriFsWriteText) {
    // Fallback to browser download if Tauri dialog is unavailable
    const payload = JSON.stringify(serializeTemplate(), null, 2);
    const blob = new Blob([payload], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = "omnipacker-template.json";
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
    setTemplateStatus(t("template.status.saved"));
    return;
  }

  try {
    const saveOptions = {
      defaultPath: settingsState.lastTemplateSaveDir
        ? `${settingsState.lastTemplateSaveDir}/omnipacker-template.json`
        : "omnipacker-template.json",
      filters: [{ name: "JSON", extensions: ["json"] }],
    };

    const filePath = await tauriDialog.save(saveOptions);
    const resolvedPath =
      typeof filePath === "string" ? filePath : filePath?.path || "";

    if (!resolvedPath) {
      // User cancelled the dialog
      return;
    }

    const payload = JSON.stringify(serializeTemplate(), null, 2);

    // Write file using Tauri's fs plugin API.
    await tauriFsWriteText(resolvedPath, payload);

    // Remember the directory for next time
    const lastSlash = Math.max(
      resolvedPath.lastIndexOf("/"),
      resolvedPath.lastIndexOf("\\"),
    );
    if (lastSlash > 0) {
      settingsState.lastTemplateSaveDir = resolvedPath.substring(0, lastSlash);
      saveSettings();
    }

    setTemplateStatus(t("template.status.saved"));
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    setTemplateStatus(t("template.error.saveFailed", { error: message }));
  }
};

const loadTemplateFromFile = (file) => {
  const reader = new FileReader();
  reader.onload = () => {
    try {
      const payload = JSON.parse(String(reader.result || ""));
      templateState.blocks = parseTemplatePayload(payload);
      persistTemplateDefault();
      setTemplateStatus(t("template.status.loaded"));
      renderTemplateBuilder();
      renderTemplatePreview();
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      setTemplateStatus(t("template.error.loadFailed", { error: message }));
    }
  };
  reader.onerror = () => {
    const message =
      reader.error && reader.error.message ? reader.error.message : "unknown";
    setTemplateStatus(t("template.error.loadFailed", { error: message }));
  };
  reader.readAsText(file);
};

let templateConfirmResolve = null;

const openTemplateResetConfirm = () => {
  if (!templateConfirmOverlay) {
    return Promise.resolve(window.confirm(t("template.confirm.save")));
  }
  if (templateConfirmMessage) {
    templateConfirmMessage.textContent = t("template.confirm.save");
  }
  if (templateConfirmResolve) {
    templateConfirmResolve(false);
    templateConfirmResolve = null;
  }
  templateConfirmOverlay.classList.add("active");
  return new Promise((resolve) => {
    templateConfirmResolve = resolve;
  });
};

const closeTemplateResetConfirm = (shouldSave) => {
  templateConfirmOverlay?.classList.remove("active");
  if (templateConfirmResolve) {
    templateConfirmResolve(shouldSave);
    templateConfirmResolve = null;
  }
};

const resetTemplateToDefault = async () => {
  const shouldSave = await openTemplateResetConfirm();
  if (shouldSave) {
    saveTemplateToFile();
  }
  templateState.blocks = createDefaultTemplate();
  await persistTemplateDefault();
  setTemplateStatus(t("template.status.reset"));
  renderTemplateBuilder();
  renderTemplatePreview();
};

const copyTemplatePreview = async () => {
  if (!templatePreviewOutput || templatePreviewOutput.classList.contains("is-error")) {
    return;
  }
  const text = templatePreviewOutput.value;
  if (!text) {
    return;
  }
  if (navigator?.clipboard?.writeText) {
    await navigator.clipboard.writeText(text);
    return;
  }
  const textarea = document.createElement("textarea");
  textarea.value = text;
  document.body.appendChild(textarea);
  textarea.select();
  document.execCommand("copy");
  document.body.removeChild(textarea);
};

const refreshQrModalText = () => {
  if (!qrModalOutput) {
    return;
  }
  if (qrWaitingTextValues.includes(qrModalOutput.textContent)) {
    updateQrModalText(t("qr.waiting"));
  }
};

const applyTranslations = () => {
  if (document?.documentElement) {
    document.documentElement.lang = settingsState.language || "en";
  }
  document.querySelectorAll("[data-i18n]").forEach((element) => {
    const key = element.dataset.i18n;
    if (key) {
      element.textContent = t(key);
    }
  });
  refreshQrModalText();
  if (templateModalOverlay?.classList.contains("active")) {
    populateTemplateBlockSelect();
    renderTemplateBuilder();
    syncTemplatePreviewMeta();
    renderTemplatePreview();
  }
  if (outputConflictOverlay?.classList.contains("active")) {
    updateOutputConflictText();
  }
};

// Load settings from localStorage on startup
const loadSettings = () => {
  try {
    const saved = localStorage.getItem("omnipacker-settings");
    if (saved) {
      const parsed = JSON.parse(saved);
      if (typeof parsed.skipCompression === "boolean") {
        settingsState.skipCompression = parsed.skipCompression;
      }
      if (typeof parsed.compressionPasswordEnabled === "boolean") {
        settingsState.compressionPasswordEnabled =
          parsed.compressionPasswordEnabled;
      }
      if (typeof parsed.compressionPassword === "string") {
        settingsState.compressionPassword = parsed.compressionPassword;
      }
      if (typeof parsed.customCompressionArgs === "string") {
        settingsState.customCompressionArgs = parsed.customCompressionArgs;
      }
      if (typeof parsed.defaultQrLogin === "boolean") {
        settingsState.defaultQrLogin = parsed.defaultQrLogin;
      }
      if (typeof parsed.language === "string") {
        settingsState.language = parsed.language;
      }
      if (parsed.defaultTemplate && typeof parsed.defaultTemplate === "object") {
        settingsState.defaultTemplate = parsed.defaultTemplate;
      }
      if (typeof parsed.lastTemplateSaveDir === "string") {
        settingsState.lastTemplateSaveDir = parsed.lastTemplateSaveDir;
      }
    }
    if (
      settingsState.compressionPasswordEnabled &&
      !settingsState.compressionPassword.trim()
    ) {
      settingsState.compressionPasswordEnabled = false;
    }
  } catch (e) {
    console.debug("[OmniPacker] Failed to load settings:", e);
  }
};

// Save settings to localStorage
const saveSettings = () => {
  try {
    localStorage.setItem("omnipacker-settings", JSON.stringify(settingsState));
  } catch (e) {
    console.debug("[OmniPacker] Failed to save settings:", e);
  }
};

const syncCompressionPasswordUI = (shouldFocus = false) => {
  if (!compressionPasswordToggle || !compressionPasswordInput) {
    return;
  }
  const enabled = compressionPasswordToggle.checked;
  compressionPasswordInput.disabled = !enabled;
  if (enabled && shouldFocus) {
    compressionPasswordInput.focus();
  }
};

const isCompressionPasswordValid = () => {
  if (!compressionPasswordToggle?.checked) {
    return true;
  }
  return Boolean(compressionPasswordInput?.value?.trim());
};

// Apply settings to UI
const applySettingsToUI = () => {
  if (skipCompressionToggle) {
    skipCompressionToggle.checked = settingsState.skipCompression;
  }
  if (compressionPasswordToggle) {
    compressionPasswordToggle.checked = settingsState.compressionPasswordEnabled;
  }
  if (compressionPasswordInput) {
    compressionPasswordInput.value = settingsState.compressionPassword;
  }
  if (customCompressionArgsInput) {
    customCompressionArgsInput.value = settingsState.customCompressionArgs;
  }
  if (defaultQrToggle) {
    defaultQrToggle.checked = settingsState.defaultQrLogin;
  }
  if (languageSelect) {
    languageSelect.value = settingsState.language;
  }
  syncCompressionPasswordUI();
  applyTranslations();
};

const applyDefaultQrLogin = () => {
  if (!qrLoginToggle) {
    return;
  }
  qrLoginToggle.checked = Boolean(settingsState.defaultQrLogin);
  const credentialsPanel = qrLoginToggle.closest(".credentials");
  credentialsPanel?.classList.toggle("qr-enabled", qrLoginToggle.checked);
  renderAll();
};

let jobSequence = 0;

const createJobId = () => {
  if (typeof crypto !== "undefined" && crypto.randomUUID) {
    return crypto.randomUUID();
  }
  jobSequence += 1;
  return `job-${Date.now()}-${jobSequence}`;
};

const getFormSnapshot = () => ({
  appId: appIdInput?.value?.trim() || "unknown",
  os: osSelect?.value || "unknown",
  branch: branchInput?.value?.trim() || "public",
  branchPassword:
    branchToggle?.checked ? branchPassword?.value || "" : "",
  username: steamUsernameInput?.value?.trim() || "",
  password: steamPasswordInput?.value || "",
  qrEnabled: Boolean(qrLoginToggle?.checked),
});

const syncAuthFromForm = (job) => {
  if (!job) {
    return;
  }
  job.username = steamUsernameInput?.value?.trim() || "";
  job.password = steamPasswordInput?.value || "";
  job.qrEnabled = Boolean(qrLoginToggle?.checked);
  job.rememberPassword = false;
};

const extractRememberedUsername = (line) => {
  if (!line) {
    return null;
  }
  const match = line.match(/-username\s+(\S+)/);
  return match ? match[1] : null;
};

const rememberQrLogin = (job, line) => {
  if (!job?.qrEnabled) {
    return;
  }
  const username = extractRememberedUsername(line);
  if (!username) {
    return;
  }
  authState.rememberedUsername = username;

  // Update current job so subsequent operations can reuse credentials
  job.username = username;
  job.rememberPassword = true;
};

const applyRememberedAuth = (job) => {
  if (!job) {
    return;
  }
  if (job.qrEnabled && authState.rememberedUsername) {
    job.qrEnabled = false;
    job.username = authState.rememberedUsername;
    job.password = "";
    job.rememberPassword = true;
    pushJobLog(job, t("auth.reuseQr", { username: authState.rememberedUsername }));
  }
};

const setSavedLogin = (login) => {
  authState.savedLogin = login;
  if (steamUsernameInput) {
    steamUsernameInput.value = login?.username ?? "";
  }
  if (steamPasswordInput) {
    steamPasswordInput.value = login?.password ?? "";
  }
};

const hasSavedLogin = () => Boolean(authState.savedLogin);

const createJob = ({
  appId,
  os,
  branch,
  branchPassword: jobBranchPassword,
  username,
  password,
  qrEnabled,
}) => {
  const job = {
    id: createJobId(),
    appId,
    os,
    branch,
    branchPassword: jobBranchPassword || "",
    username,
    password,
    qrEnabled,
    rememberPassword: false,
    status: "queued",
    logs: [],
    compressionProgress: null,
    qrText: null,
    qrCaptureActive: false,
    qrCaptureLines: [],
    steamGuardEmailPending: false,
    backendJobId: null, // Job ID assigned by backend (staging directory name)
    stagingDir: null, // Staging directory path
  };
  jobState.jobs.set(job.id, job);
  jobState.order.push(job.id);
  return job;
};

const getRunningJob = () =>
  jobState.runningJobId
    ? jobState.jobs.get(jobState.runningJobId) || null
    : null;

const getSelectedJob = () =>
  jobState.selectedJobId
    ? jobState.jobs.get(jobState.selectedJobId) || null
    : null;

const getJobByBackendId = (backendJobId) => {
  if (!backendJobId) {
    return null;
  }
  for (const job of jobState.jobs.values()) {
    if (job.backendJobId === backendJobId) {
      return job;
    }
  }
  return null;
};

const resolveEventJob = (payload) => {
  const backendJobId = payload?.jobId;
  if (backendJobId) {
    const matchingJob = getJobByBackendId(backendJobId);
    if (matchingJob) {
      return matchingJob;
    }
    const runningJob = getRunningJob();
    if (runningJob && !runningJob.backendJobId) {
      runningJob.backendJobId = backendJobId;
      return runningJob;
    }
  }
  return getRunningJob();
};

const getNextQueuedJob = () => {
  for (const jobId of jobState.order) {
    const job = jobState.jobs.get(jobId);
    if (job?.status === "queued") {
      return job;
    }
  }
  return null;
};

const hasQueuedJobs = () =>
  jobState.order.some((jobId) => jobState.jobs.get(jobId)?.status === "queued");

const isRunningJob = (jobId) => jobState.runningJobId === jobId;
const isQueueRunning = () => Boolean(jobState.runningJobId);

const updateFormInputState = () => {
  const running = isQueueRunning();
  const qrEnabled = Boolean(qrLoginToggle?.checked);
  const loginLocked = hasSavedLogin();

  // Disable form inputs
  if (appIdInput) appIdInput.disabled = running;
  if (osSelect) osSelect.disabled = running;
  if (branchInput) branchInput.disabled = running;
  if (branchPassword) branchPassword.disabled = running || !branchToggle?.checked;
  if (addToQueueButton) addToQueueButton.disabled = running;

  // Disable credentials (QR or saved login lock)
  if (steamUsernameInput) {
    steamUsernameInput.disabled = running || qrEnabled || loginLocked;
  }
  if (steamPasswordInput) {
    steamPasswordInput.disabled = running || qrEnabled || loginLocked;
  }

  // Visual feedback
  const gameManager = document.querySelector(".game-manager");
  const branchManager = document.querySelector('[aria-label="Branch Manager"]');
  const credentials = document.querySelector(".credentials");

  if (gameManager) gameManager.classList.toggle("form-inputs-disabled", running);
  if (branchManager) branchManager.classList.toggle("form-inputs-disabled", running);
  if (credentials) credentials.classList.toggle("form-inputs-disabled", running);
};

const moveJob = (jobId, direction) => {
  if (isQueueRunning()) {
    return;
  }
  if (isRunningJob(jobId)) {
    return;
  }
  const index = jobState.order.indexOf(jobId);
  if (index === -1) {
    return;
  }
  const nextIndex = index + direction;
  if (nextIndex < 0 || nextIndex >= jobState.order.length) {
    return;
  }
  const order = jobState.order;
  [order[index], order[nextIndex]] = [order[nextIndex], order[index]];
};

const removeJob = (jobId) => {
  if (isQueueRunning()) {
    return;
  }
  if (isRunningJob(jobId)) {
    return;
  }
  const index = jobState.order.indexOf(jobId);
  if (index === -1) {
    return;
  }
  jobState.order.splice(index, 1);
  jobState.jobs.delete(jobId);

  if (jobState.selectedJobId === jobId) {
    if (jobState.order.length === 0) {
      jobState.selectedJobId = null;
      return;
    }
    const nextIndex = Math.min(index, jobState.order.length - 1);
    jobState.selectedJobId = jobState.order[nextIndex] ?? null;
  }
};

const clearQueuedJobs = () => {
  if (isQueueRunning()) {
    alert(t("alert.clearQueue"));
    return;
  }

  if (jobState.order.length === 0) {
    return;
  }

  // Clear all jobs
  jobState.jobs.clear();
  jobState.order = [];
  jobState.selectedJobId = null;
};

const trimJobLogs = (job) => {
  if (job.logs.length <= LOG_LINE_CAP + LOG_TRIM_MARGIN) {
    return false;
  }
  const trimCount = job.logs.length - LOG_LINE_CAP;
  job.logs.splice(0, trimCount);
  return true;
};

const scheduleConsoleRender = () => {
  if (!consoleOutput || activeTab !== "console") {
    return;
  }
  if (consoleRenderState.timer) {
    return;
  }
  consoleRenderState.timer = window.setTimeout(() => {
    consoleRenderState.timer = null;
    renderConsole();
  }, CONSOLE_FLUSH_INTERVAL_MS);
};

const debugConsoleLog = (message) => {
  if (!tauriInvoke) {
    return;
  }
  void tauriInvoke("debug_console_log", { line: message }).catch(() => {});
};

const pushJobLog = (job, message, options = {}) => {
  if (!job) {
    return;
  }
  const { forwardToDebug = true } = options;
  job.logs.push(message);
  const trimmed = trimJobLogs(job);
  if (forwardToDebug) {
    debugConsoleLog(message);
  }
  if (job.id === jobState.selectedJobId) {
    if (trimmed) {
      consoleRenderState.needsFullRender = true;
    }
    scheduleConsoleRender();
  }
};

const appendLogToJob = (job, payload) => {
  const line = payload?.line ?? "";
  const stream = payload?.stream ? `[${payload.stream}] ` : "";
  pushJobLog(job, `${stream}${line}`, { forwardToDebug: false });
};

const appendSystemMessage = (message) => {
  const selectedJob = getSelectedJob();
  if (selectedJob) {
    pushJobLog(selectedJob, message);
    renderAll();
    return;
  }
  if (consoleOutput) {
    consoleOutput.value = message;
    debugConsoleLog(message);
    return;
  }
  debugConsoleLog(message);
  console.debug(`[OmniPacker] ${message}`);
};

const updateQrModalText = (text) => {
  if (!qrModalOutput) {
    return;
  }
  qrModalOutput.textContent = text;
};

const openQrModal = () => {
  qrModalOverlay?.classList.add("active");
};

const closeQrModal = () => {
  qrModalOverlay?.classList.remove("active");
};

const openSettingsModal = () => {
  applySettingsToUI();
  settingsModalOverlay?.classList.add("active");
};

const closeSettingsModal = () => {
  if (!isCompressionPasswordValid()) {
    window.alert(t("settings.compressionPasswordRequired"));
    compressionPasswordInput?.focus();
    return;
  }
  settingsModalOverlay?.classList.remove("active");
};

const openSteamGuardModal = () => {
  steamGuardModalOverlay?.classList.add("active");
};

const closeSteamGuardModal = () => {
  steamGuardModalOverlay?.classList.remove("active");
};

const setSteamGuardEmailBusy = (busy) => {
  steamGuardEmailState.busy = busy;
  if (steamGuardEmailInput) {
    steamGuardEmailInput.disabled = busy;
  }
  if (steamGuardEmailSubmitButton) {
    steamGuardEmailSubmitButton.disabled = busy;
  }
};

const updateSteamGuardEmailMessage = (line) => {
  if (!steamGuardEmailMessage) {
    return;
  }
  const provider = extractSteamGuardEmailProvider(line);
  steamGuardEmailState.provider = provider;
  steamGuardEmailMessage.textContent = `STEAM GUARD! Please enter the auth code sent to the email at ${provider}`;
};

const openSteamGuardEmailModal = (line, jobId) => {
  steamGuardEmailState.jobId = jobId || null;
  setSteamGuardEmailBusy(false);
  updateSteamGuardEmailMessage(line);
  if (steamGuardEmailInput) {
    steamGuardEmailInput.value = "";
  }
  if (steamGuardEmailStatus) {
    steamGuardEmailStatus.textContent = t("steamGuard.email.status");
  }
  steamGuardEmailOverlay?.classList.add("active");
  steamGuardEmailInput?.focus();
};

const closeSteamGuardEmailModal = () => {
  const job = steamGuardEmailState.jobId
    ? jobState.jobs.get(steamGuardEmailState.jobId)
    : null;
  if (job) {
    job.steamGuardEmailPending = false;
  }
  steamGuardEmailOverlay?.classList.remove("active");
  setSteamGuardEmailBusy(false);
  steamGuardEmailState.jobId = null;
  steamGuardEmailState.provider = "";
  if (steamGuardEmailInput) {
    steamGuardEmailInput.value = "";
  }
};

const resetJobForRetry = (job) => {
  if (!job) {
    return;
  }
  job.status = "queued";
  job.backendJobId = null;
  job.stagingDir = null;
  job.compressionProgress = null;
  job.qrText = null;
  job.qrCaptureActive = false;
  job.qrCaptureLines = [];
  job.steamGuardPending = false;
  job.steamGuardEmailPending = false;
};

const requestSteamGuardEmailRetry = (job) => {
  if (!job || steamGuardEmailRetryState.requested) {
    return;
  }
  steamGuardEmailRetryState.requested = true;
  steamGuardEmailRetryState.jobId = job.id;
  job.steamGuardEmailPending = false;
  pushJobLog(job, t("steamGuard.email.incorrect"));
  closeSteamGuardEmailModal();
  renderAll();

  if (!tauriInvoke) {
    pushJobLog(job, t("auth.tauriUnavailable"));
    steamGuardEmailRetryState.requested = false;
    steamGuardEmailRetryState.jobId = null;
    return;
  }

  void tauriInvoke("cancel_depotdownloader").catch((error) => {
    pushJobLog(job, t("job.cancelFailed", { error }));
    steamGuardEmailRetryState.requested = false;
    steamGuardEmailRetryState.jobId = null;
    renderAll();
  });
};

const submitSteamGuardEmailCode = async () => {
  const code = steamGuardEmailInput?.value?.trim() ?? "";
  if (!code) {
    if (steamGuardEmailStatus) {
      steamGuardEmailStatus.textContent = t("steamGuard.email.empty");
    }
    return;
  }

  if (!tauriInvoke) {
    if (steamGuardEmailStatus) {
      steamGuardEmailStatus.textContent = t("auth.tauriUnavailable");
    }
    return;
  }

  const job = steamGuardEmailState.jobId
    ? jobState.jobs.get(steamGuardEmailState.jobId)
    : getRunningJob();

  try {
    setSteamGuardEmailBusy(true);
    await tauriInvoke(
      enumState.active ? "submit_enum_steam_guard_code" : "submit_steam_guard_code",
      { code },
    );
    if (job) {
      pushJobLog(job, t("steamGuard.email.sent"));
      job.steamGuardEmailPending = false;
    }
    closeSteamGuardEmailModal();
  } catch (error) {
    if (job) {
      pushJobLog(job, t("steamGuard.email.failed", { error }));
    }
    if (steamGuardEmailStatus) {
      steamGuardEmailStatus.textContent = t("steamGuard.email.failed", { error });
    }
    setSteamGuardEmailBusy(false);
  }
};

const updateOutputConflictText = () => {
  if (!outputConflictMessage || !outputConflictPath) {
    return;
  }
  const name =
    outputConflictState.outputName ||
    outputConflictState.outputPath ||
    t("output.conflict.title");
  outputConflictMessage.textContent = t("output.conflict.message", { name });
  outputConflictPath.textContent = t("output.conflict.path", {
    path: outputConflictState.outputPath,
  });
};

const setOutputConflictBusy = (busy) => {
  outputConflictState.busy = busy;
  if (outputConflictOverwriteButton) {
    outputConflictOverwriteButton.disabled = busy;
  }
  if (outputConflictCopyButton) {
    outputConflictCopyButton.disabled = busy;
  }
  if (outputConflictCancelButton) {
    outputConflictCancelButton.disabled = busy;
  }
};

const openOutputConflictModal = (payload) => {
  outputConflictState.jobId = payload?.jobId ?? null;
  outputConflictState.outputName = payload?.outputName ?? "";
  outputConflictState.outputPath = payload?.outputPath ?? "";
  setOutputConflictBusy(false);
  updateOutputConflictText();
  outputConflictOverlay?.classList.add("active");
};

const closeOutputConflictModal = () => {
  outputConflictOverlay?.classList.remove("active");
  setOutputConflictBusy(false);
  outputConflictState.jobId = null;
  outputConflictState.outputName = "";
  outputConflictState.outputPath = "";
};

const sendOutputConflictChoice = async (choice) => {
  const jobId = outputConflictState.jobId;
  if (!jobId) {
    closeOutputConflictModal();
    return;
  }

  const job = getJobByBackendId(jobId) || getRunningJob();

  if (!tauriInvoke) {
    if (job) {
      pushJobLog(
        job,
        t("output.conflict.resolveError", {
          error: t("auth.tauriUnavailable"),
        }),
      );
      renderAll();
    }
    closeOutputConflictModal();
    return;
  }

  try {
    setOutputConflictBusy(true);
    await tauriInvoke("resolve_output_conflict", { jobId, choice });
    if (job) {
      pushJobLog(job, t(`output.conflict.choice.${choice}`));
      renderAll();
    }
  } catch (error) {
    if (job) {
      pushJobLog(job, t("output.conflict.resolveError", { error }));
      renderAll();
    }
  }

  closeOutputConflictModal();
};

// Detects Steam Guard mobile app confirmation prompt
const steamGuardPromptMatches = (line) => {
  return line.includes("STEAM GUARD!") && line.includes("Steam Mobile App");
};

const steamGuardEmailPromptMatches = (line) => {
  const lower = line.toLowerCase();
  return (
    lower.includes("steam guard") &&
    lower.includes("auth code sent to the email at")
  );
};

const steamGuardEmailFailureMatches = (line) =>
  line.includes("No code was provided by the authenticator");

const steamGuardEmailInvalidCodeMatches = (line) => {
  const lower = line.toLowerCase();
  return lower.includes(
    "previous 2-factor auth code you have provided is incorrect",
  );
};

const extractSteamGuardEmailProvider = (line) => {
  const match = line.match(/email at ([^:]+):?/i);
  return match ? match[1].trim() : "email";
};

// Detects successful login confirmation after Steam Guard
const steamGuardConfirmedMatches = (line) => {
  return line.trim() === "Done!" || line.includes("[stdout]  Done!");
};

const qrStartMatches = (line) => {
  const lower = line.toLowerCase();
  return lower.includes("steam mobile app") && lower.includes("qr code");
};

const isQrArtLine = (line) => {
  if (!line) {
    return false;
  }
  if (/[█▀▄]/.test(line)) {
    return true;
  }
  return /^[\s]+$/.test(line) && line.length >= 10;
};

const updateQrCapture = (job, payload) => {
  if (!job.qrEnabled || payload?.stream !== "stdout") {
    return;
  }
  const line = payload?.line ?? "";

  if (!job.qrCaptureActive && qrStartMatches(line)) {
    job.qrCaptureActive = true;
    job.qrCaptureLines = [];
    openQrModal();
    updateQrModalText(t("qr.waiting"));
    return;
  }

  if (!job.qrCaptureActive) {
    return;
  }

  if (isQrArtLine(line)) {
    job.qrCaptureLines.push(line);
    const trimmed = [...job.qrCaptureLines];
    while (trimmed.length && trimmed[0].trim() === "") {
      trimmed.shift();
    }
    while (trimmed.length && trimmed[trimmed.length - 1].trim() === "") {
      trimmed.pop();
    }
    job.qrText = trimmed.join("\n");
    updateQrModalText(job.qrText);
    return;
  }

  if (job.qrCaptureLines.length > 0) {
    job.qrCaptureActive = false;
  }
};

const qrLoginSuccessMatches = (line) =>
  line.includes("Success! Next time you can login with -username");

const mapStatusToJobState = (payload) => {
  if (!payload?.status) {
    return null;
  }
  switch (payload.status) {
    case "starting":
      return "running";
    case "resolving_metadata":
      return "running"; // Metadata resolution is part of the running phase
    case "running":
      return "running";
    case "finalizing":
      return "running";
    case "compressing":
      return "compressing";
    case "completed":
      return "done";
    case "finalization_failed":
      return "failed";
    case "error":
      return "failed";
    case "exited":
      return payload?.code === 0 ? "done" : "failed";
    default:
      return null;
  }
};

const extractCompressionPercent = (line) => {
  const match = line.match(/(\d{1,3})%/);
  if (!match) {
    return null;
  }
  const value = Number(match[1]);
  if (!Number.isFinite(value) || value < 0 || value > 100) {
    return null;
  }
  return value;
};

const updateCompressionProgress = (job, line) => {
  const percent = extractCompressionPercent(line);
  if (percent !== null) {
    setCompressionProgress(job, percent);
  }
};

const setCompressionProgress = (job, percent) => {
  if (!Number.isFinite(percent) || percent < 0 || percent > 100) {
    return;
  }
  if (job.compressionProgress !== percent) {
    job.compressionProgress = percent;
    if (job.status === "compressing") {
      renderQueue();
    }
  }
};

const setActiveTab = (name) => {
  activeTab = name;
  tabs.forEach((tab) => {
    tab.classList.toggle("active", tab.dataset.tab === name);
  });
  panels.forEach((panel) => {
    panel.classList.toggle("active", panel.id === `tab-${name}`);
  });
  if (name === "console") {
    consoleRenderState.needsFullRender = true;
    renderConsole(true);
  }
};

tabs.forEach((tab) => {
  tab.addEventListener("click", () => {
    setActiveTab(tab.dataset.tab);
  });
});

if (branchToggle && branchPassword) {
  const syncBranchPassword = () => {
    branchPassword.disabled = !branchToggle.checked;
  };
  branchToggle.addEventListener("change", syncBranchPassword);
  syncBranchPassword();
}

const tauriEvent = window.__TAURI__?.event;
const tauriInvoke = window.__TAURI__?.core?.invoke;

const syncTemplateStorage = async () => {
  if (!tauriInvoke) {
    return;
  }

  let backendPayload = null;
  try {
    backendPayload = await tauriInvoke("load_template_data");
  } catch (error) {
    console.debug("[OmniPacker] Failed to load backend template:", error);
  }

  const localPayload = settingsState.defaultTemplate;
  const localValid = isValidTemplatePayload(localPayload);
  const backendValid = isValidTemplatePayload(backendPayload);

  if (!localValid && backendValid) {
    settingsState.defaultTemplate = backendPayload;
    saveSettings();
    return;
  }

  let payloadToSave = localValid ? localPayload : null;
  if (!payloadToSave) {
    payloadToSave = serializeTemplateBlocks(createDefaultTemplate());
    settingsState.defaultTemplate = payloadToSave;
    saveSettings();
  }

  try {
    await tauriInvoke("save_template_data", { templatePayload: payloadToSave });
  } catch (error) {
    console.debug("[OmniPacker] Failed to save template to backend:", error);
  }
};

const warnOrphanEvent = (eventName, payload) => {
  console.debug(
    `[OmniPacker] Dropped ${eventName} (no running job).`,
    payload,
  );
};

if (tauriEvent?.listen) {
  tauriEvent.listen("dd:log", (event) => {
    const job = resolveEventJob(event.payload);
    if (!job) {
      warnOrphanEvent("dd:log", event.payload);
      return;
    }
    appendLogToJob(job, event.payload);
    updateQrCapture(job, event.payload);
    if (
      event.payload?.line?.includes(
        "Couldn't find any depots to download for app",
      )
    ) {
      pushJobLog(job, t("dd.noDepots"));
    }
    if (job.qrEnabled && qrLoginSuccessMatches(event.payload?.line ?? "")) {
      rememberQrLogin(job, event.payload?.line ?? "");
      closeQrModal();
    }

    // Steam Guard mobile app confirmation detection
    const logLine = event.payload?.line ?? "";
    if (steamGuardPromptMatches(logLine)) {
      job.steamGuardPending = true;
      openSteamGuardModal();
    }
    if (job.steamGuardPending && steamGuardConfirmedMatches(logLine)) {
      job.steamGuardPending = false;
      closeSteamGuardModal();
    }

    if (steamGuardEmailPromptMatches(logLine)) {
      job.steamGuardEmailPending = true;
      openSteamGuardEmailModal(logLine, job.id);
    }
    if (steamGuardEmailFailureMatches(logLine)) {
      job.steamGuardEmailPending = false;
      closeSteamGuardEmailModal();
    }
    if (steamGuardEmailInvalidCodeMatches(logLine)) {
      requestSteamGuardEmailRetry(job);
    }
    scheduleConsoleRender();
  });

  tauriEvent.listen("dd:status", (event) => {
    const job = resolveEventJob(event.payload);
    if (!job) {
      warnOrphanEvent("dd:status", event.payload);
      return;
    }
    const nextStatus = mapStatusToJobState(event.payload);
    if (nextStatus) {
      const wasRunning = jobState.runningJobId === job.id;
      const retryRequested =
        steamGuardEmailRetryState.requested &&
        steamGuardEmailRetryState.jobId === job.id;
      const isTerminal = nextStatus === "done" || nextStatus === "failed";
      if (retryRequested && wasRunning && isTerminal) {
        resetJobForRetry(job);
        jobState.runningJobId = null;
        steamGuardEmailRetryState.requested = false;
        steamGuardEmailRetryState.jobId = null;
        closeQrModal();
        closeSteamGuardModal();
        closeSteamGuardEmailModal();
        renderAll();
        void startJob();
        return;
      }
      const previousStatus = job.status;
      job.status = nextStatus;
      if (nextStatus === "compressing") {
        if (!Number.isFinite(job.compressionProgress)) {
          job.compressionProgress = 0;
        }
      } else if (previousStatus === "compressing") {
        job.compressionProgress = null;
      }
      if (wasRunning && (nextStatus === "done" || nextStatus === "failed")) {
        jobState.runningJobId = null;
        closeQrModal();
        closeSteamGuardModal();
        closeSteamGuardEmailModal();
      }
      renderAll();
      if (wasRunning && (nextStatus === "done" || nextStatus === "failed")) {
        const nextJob = getNextQueuedJob();
        if (nextJob) {
          void startJob();
        } else {
          authState.rememberedUsername = null;
        }
      }
    }
  });

  tauriEvent.listen("dd:output_conflict", (event) => {
    const payload = event.payload ?? {};
    const job = resolveEventJob(payload);
    if (!job) {
      warnOrphanEvent("dd:output_conflict", payload);
    } else {
      pushJobLog(job, t("output.conflict.log", { path: payload.outputPath || "" }));
      renderAll();
    }
    openOutputConflictModal(payload);
  });

  // Listen for 7-Zip logs during compression
  tauriEvent.listen("7z:log", (event) => {
    const job = getRunningJob();
    if (!job) {
      warnOrphanEvent("7z:log", event.payload);
      return;
    }

    const line = event.payload?.line ?? "";
    const stream = event.payload?.stream ? `[7z:${event.payload.stream}] ` : "[7z] ";
    pushJobLog(job, `${stream}${line}`, { forwardToDebug: false });
    updateCompressionProgress(job, line);
    scheduleConsoleRender();
  });

  tauriEvent.listen("7z:progress", (event) => {
    const job = getRunningJob();
    if (!job) {
      warnOrphanEvent("7z:progress", event.payload);
      return;
    }
    const percent = Number(event.payload?.percent);
    if (!Number.isFinite(percent)) {
      return;
    }
    setCompressionProgress(job, percent);
  });

  // Listen for 7-Zip status (for debugging)
  tauriEvent.listen("7z:status", (event) => {
    const job = getRunningJob();
    if (!job) {
      warnOrphanEvent("7z:status", event.payload);
      return;
    }

    if (event.payload?.status) {
      pushJobLog(job, t("zip.status", { status: event.payload.status }));
      scheduleConsoleRender();
    }
  });
}

// Build job metadata object for backend
const buildJobMetadata = (job) => ({
  appId: job.appId || "unknown",
  os: job.os || "Windows x64",
  branch: job.branch || "public",
  branchPassword: job.branchPassword || "",
  username: job.username || "",
  password: job.password || "",
  qrEnabled: Boolean(job.qrEnabled),
  rememberPassword: Boolean(job.rememberPassword),
  skipCompression: settingsState.skipCompression,
  compressionPasswordEnabled: settingsState.compressionPasswordEnabled,
  compressionPassword: settingsState.compressionPassword,
  customCompressionArgs: settingsState.customCompressionArgs,
});

const startJob = async () => {
  if (jobState.runningJobId) {
    const selectedJob = getSelectedJob();
    const message = t("job.runningMessage");
    if (selectedJob) {
      pushJobLog(selectedJob, message);
      renderAll();
    } else {
      console.debug(`[OmniPacker] ${message}`);
    }
    return;
  }

  const selectedJob = getSelectedJob();
  const jobToRun = getNextQueuedJob();

  if (!jobToRun) {
    if (selectedJob) {
      pushJobLog(selectedJob, t("job.noQueued"));
      renderAll();
    } else {
      console.debug("[OmniPacker] No queued jobs.");
    }
    return;
  }

  syncAuthFromForm(jobToRun);
  applyRememberedAuth(jobToRun);
  jobState.runningJobId = jobToRun.id;
  jobState.selectedJobId = jobToRun.id;
  renderAll();

  if (jobToRun.qrEnabled) {
    openQrModal();
    updateQrModalText(t("qr.waiting"));
  }

  if (!tauriInvoke) {
    jobToRun.status = "failed";
    pushJobLog(jobToRun, t("job.tauriUnavailable"));
    console.debug("[OmniPacker] Tauri invoke unavailable for Start.");
    jobState.runningJobId = null;
    closeQrModal();
    closeSteamGuardModal();
    renderAll();
    return;
  }

  await syncTemplateStorage();

  try {
    const jobMetadata = buildJobMetadata(jobToRun);
    try {
      const outputFolder = await tauriInvoke("get_output_folder");
      if (outputFolder) {
        pushJobLog(jobToRun, t("job.outputDir", { path: outputFolder }));
        renderAll();
      }
    } catch (error) {
      pushJobLog(jobToRun, t("job.outputDirError", { error }));
      renderAll();
    }
    pushJobLog(jobToRun, t("job.selectedOs", { os: formatOsLabel(jobToRun.os) }));
    pushJobLog(jobToRun, t("job.starting", { appId: jobToRun.appId }));
    renderAll();
    // Backend returns the job_id (staging directory name)
    const backendJobId = await tauriInvoke("run_depotdownloader", { job: jobMetadata });
    jobToRun.backendJobId = backendJobId;
  } catch (error) {
    jobToRun.status = "failed";
    pushJobLog(jobToRun, t("job.startFailed", { error }));
    jobState.runningJobId = null;
    renderAll();
  }
};

const cancelJob = async () => {
  if (!jobState.runningJobId) {
    console.debug("[OmniPacker] No job is running.");
    return;
  }

  const job = jobState.jobs.get(jobState.runningJobId);
  if (!job) {
    console.warn("[OmniPacker] Running job not found in state.");
    return;
  }

  try {
    pushJobLog(job, t("job.canceling"));
    renderAll();

    const command = job.status === "compressing" ? "cancel_7zip" : "cancel_depotdownloader";
    await tauriInvoke(command);

    pushJobLog(job, t("job.cancelled"));
    renderAll();
  } catch (error) {
    const errorMsg = t("job.cancelFailed", { error });
    pushJobLog(job, errorMsg);
    console.error("[OmniPacker]", errorMsg);
    renderAll();
  }
};

const updateStartButtonState = () => {
  if (!startButton) return;

  if (isQueueRunning()) {
    startButton.textContent = t("action.cancel");
    startButton.classList.add("cancel-mode");
  } else {
    startButton.textContent = t("action.start");
    startButton.classList.remove("cancel-mode");
  }
};

if (startButton) {
  startButton.addEventListener("click", () => {
    if (isQueueRunning()) {
      void cancelJob();
    } else {
      void startJob();
    }
  });
}

const openOutputFolder = async () => {
  if (!tauriInvoke) {
    appendSystemMessage(t("output.unavailable"));
    return;
  }

  try {
    await tauriInvoke("open_output_folder");
  } catch (error) {
    appendSystemMessage(t("output.failed", { error }));
  }
};

if (openOutputButton) {
  openOutputButton.addEventListener("click", () => {
    void openOutputFolder();
  });
}

const saveLoginDetails = async () => {
  const username = steamUsernameInput?.value?.trim() ?? "";
  const password = steamPasswordInput?.value ?? "";

  if (!username || !password) {
    alert(t("auth.saveMissing"));
    return;
  }

  if (!tauriInvoke) {
    alert(t("auth.saveFailed", { error: t("auth.tauriUnavailable") }));
    return;
  }

  try {
    await tauriInvoke("save_login_data", { username, password });
    setSavedLogin({ username, password });
    renderAll();
  } catch (error) {
    alert(t("auth.saveFailed", { error: String(error) }));
  }
};

const loadSavedLoginDetails = async () => {
  if (!tauriInvoke) {
    return;
  }

  try {
    const saved = await tauriInvoke("load_login_data");
    if (saved?.username && saved?.password) {
      setSavedLogin(saved);
      renderAll();
    }
  } catch (error) {
    console.debug("[OmniPacker] Failed to load login data:", error);
  }
};

const deleteSavedLoginDetails = async () => {
  if (!tauriInvoke) {
    alert(t("auth.deleteFailed", { error: t("auth.tauriUnavailable") }));
    return;
  }

  try {
    await tauriInvoke("delete_login_data");
    setSavedLogin(null);
    renderAll();
  } catch (error) {
    alert(t("auth.deleteFailed", { error: String(error) }));
  }
};

const addJobToQueue = () => {
  const job = createJob(getFormSnapshot());
  job.status = "queued";
  jobState.selectedJobId = job.id;
  enumState.showInConsole = false;
  if (appIdInput) {
    appIdInput.value = "";
  }
  if (branchInput) {
    branchInput.value = "public";
  }
  renderAll();
};

if (addToQueueButton) {
  addToQueueButton.addEventListener("click", addJobToQueue);
}

if (clearQueueButton) {
  clearQueueButton.addEventListener("click", () => {
    clearQueuedJobs();
    renderAll();
  });
}

if (appIdInput) {
  appIdInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      addJobToQueue();
    }
  });
}

const copyQrText = async () => {
  const selectedJob = getSelectedJob();
  const text = selectedJob?.qrText;
  if (!text) {
    return;
  }
  if (navigator?.clipboard?.writeText) {
    await navigator.clipboard.writeText(text);
    return;
  }
  const textarea = document.createElement("textarea");
  textarea.value = text;
  document.body.appendChild(textarea);
  textarea.select();
  document.execCommand("copy");
  document.body.removeChild(textarea);
};

const renderQueue = () => {
  if (!queueList) {
    return;
  }

  queueList.innerHTML = "";

  if (jobState.order.length === 0) {
    const empty = document.createElement("div");
    empty.className = "queue-empty";
    empty.textContent = t("queue.empty");
    queueList.appendChild(empty);
    if (clearQueueButton) {
      clearQueueButton.disabled = true;
    }
    return;
  }

  if (clearQueueButton) {
    clearQueueButton.disabled = isQueueRunning();
  }

  for (let index = 0; index < jobState.order.length; index += 1) {
    const jobId = jobState.order[index];
    const job = jobState.jobs.get(jobId);
    if (!job) {
      continue;
    }
    const row = document.createElement("div");
    row.setAttribute("role", "option");
    row.tabIndex = 0;
    row.className = "queue-item";
    row.dataset.jobId = job.id;

    if (job.id === jobState.selectedJobId) {
      row.classList.add("selected");
    }

    if (job.status === "failed") {
      row.classList.add("failed");
    }

    const header = document.createElement("div");
    header.className = "queue-item-header";
    header.textContent = t("queue.appId", { appId: job.appId });

    const top = document.createElement("div");
    top.className = "queue-item-top";

    const status = document.createElement("div");
    status.className = `queue-item-status status-${job.status}`;
    let statusText = formatStatus(job.status);
    if (job.status === "compressing") {
      const progress = Number.isFinite(job.compressionProgress)
        ? job.compressionProgress
        : 0;
      statusText = `${statusText} ${progress}%`;
    }
    status.textContent = statusText;

    const controls = document.createElement("div");
    controls.className = "queue-item-controls";

    const isRunning = isRunningJob(job.id);
    const queueRunning = isQueueRunning();
    const disableReason = queueRunning
      ? t("queue.running")
      : t("queue.noReorder");

    const upButton = document.createElement("button");
    upButton.type = "button";
    upButton.className = "queue-btn queue-btn-up";
    upButton.textContent = "▲";
    upButton.disabled = queueRunning || isRunning || index === 0;
    if (upButton.disabled) {
      upButton.title = queueRunning
        ? disableReason
        : isRunning
          ? disableReason
          : t("queue.atTop");
    }
    upButton.addEventListener("click", (event) => {
      event.stopPropagation();
      moveJob(job.id, -1);
      renderAll();
    });

    const downButton = document.createElement("button");
    downButton.type = "button";
    downButton.className = "queue-btn queue-btn-down";
    downButton.textContent = "▼";
    downButton.disabled =
      queueRunning || isRunning || index === jobState.order.length - 1;
    if (downButton.disabled) {
      downButton.title = queueRunning
        ? disableReason
        : isRunning
          ? disableReason
          : t("queue.atBottom");
    }
    downButton.addEventListener("click", (event) => {
      event.stopPropagation();
      moveJob(job.id, 1);
      renderAll();
    });

    const removeButton = document.createElement("button");
    removeButton.type = "button";
    removeButton.className = "queue-btn queue-btn-trash";
    removeButton.textContent = "🗑";
    removeButton.disabled = queueRunning || isRunning;
    if (removeButton.disabled) {
      removeButton.title = disableReason;
    }
    removeButton.addEventListener("click", (event) => {
      event.stopPropagation();
      removeJob(job.id);
      renderAll();
    });

    controls.appendChild(upButton);
    controls.appendChild(downButton);
    controls.appendChild(removeButton);

    top.appendChild(status);
    top.appendChild(controls);

    const meta = document.createElement("div");
    meta.className = "queue-item-meta";
    meta.textContent = t("queue.meta", {
      branch: job.branch || "public",
      os: formatOsLabel(job.os),
    });

    row.appendChild(header);
    row.appendChild(top);
    row.appendChild(meta);

    row.addEventListener("click", () => {
      jobState.selectedJobId = job.id;
      enumState.showInConsole = false; // selecting a job reverts the Console view
      renderAll();
    });
    row.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        jobState.selectedJobId = job.id;
        enumState.showInConsole = false;
        renderAll();
      }
    });

    queueList.appendChild(row);
  }
};

const renderConsole = (force = false) => {
  if (!consoleOutput) {
    return;
  }
  if (!force && activeTab !== "console") {
    return;
  }

  if (consoleRenderState.timer) {
    window.clearTimeout(consoleRenderState.timer);
    consoleRenderState.timer = null;
  }

  // Library (owned-app enumeration) has no queue job, so surface its live log
  // here as the debug view until the user selects a queue job.
  if (enumState.showInConsole) {
    consoleOutput.value = enumState.log.length
      ? enumState.log.join("\n")
      : "Library: waiting for DepotDownloader output…";
    consoleOutput.scrollTop = consoleOutput.scrollHeight;
    consoleRenderState.jobId = "__enum__";
    consoleRenderState.renderedLines = 0;
    consoleRenderState.needsFullRender = true;
    return;
  }

  const selectedJob = getSelectedJob();
  if (!selectedJob) {
    consoleOutput.value = t("console.noSelection");
    consoleRenderState.jobId = null;
    consoleRenderState.renderedLines = 0;
    consoleRenderState.needsFullRender = true;
    return;
  }

  if (consoleRenderState.jobId !== selectedJob.id) {
    consoleRenderState.jobId = selectedJob.id;
    consoleRenderState.renderedLines = 0;
    consoleRenderState.needsFullRender = true;
  }

  if (consoleRenderState.renderedLines > selectedJob.logs.length) {
    consoleRenderState.needsFullRender = true;
  }

  if (selectedJob.logs.length === 0) {
    consoleOutput.value = "";
    consoleRenderState.renderedLines = 0;
    consoleRenderState.needsFullRender = false;
    return;
  }

  if (consoleRenderState.needsFullRender) {
    consoleOutput.value = selectedJob.logs.join("\n");
    consoleRenderState.renderedLines = selectedJob.logs.length;
    consoleRenderState.needsFullRender = false;
  } else if (consoleRenderState.renderedLines < selectedJob.logs.length) {
    const nextLines = selectedJob.logs.slice(consoleRenderState.renderedLines);
    const addition = nextLines.join("\n");
    consoleOutput.value = consoleOutput.value
      ? `${consoleOutput.value}\n${addition}`
      : addition;
    consoleRenderState.renderedLines = selectedJob.logs.length;
  }

  // Auto-scroll to bottom so user sees latest output
  consoleOutput.scrollTop = consoleOutput.scrollHeight;
};

const renderAll = () => {
  renderQueue();
  renderConsole();
  updateFormInputState();
  updateStartButtonState();
};

if (qrLoginToggle) {
  const syncQrLogin = () => {
    const enabled = qrLoginToggle.checked;
    const credentialsPanel = qrLoginToggle.closest(".credentials");
    credentialsPanel?.classList.toggle("qr-enabled", enabled);
    renderAll();
  };
  qrLoginToggle.addEventListener("change", syncQrLogin);
  syncQrLogin();
}

if (qrCloseButton) {
  qrCloseButton.addEventListener("click", closeQrModal);
}

if (qrCopyButton) {
  qrCopyButton.addEventListener("click", () => {
    void copyQrText();
  });
}

if (steamGuardEmailSubmitButton) {
  steamGuardEmailSubmitButton.addEventListener("click", () => {
    void submitSteamGuardEmailCode();
  });
}

if (steamGuardEmailCloseButton) {
  steamGuardEmailCloseButton.addEventListener("click", closeSteamGuardEmailModal);
}

if (steamGuardEmailInput) {
  steamGuardEmailInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      void submitSteamGuardEmailCode();
    }
  });
}

if (saveLoginButton) {
  saveLoginButton.addEventListener("click", () => {
    void saveLoginDetails();
  });
}

// Settings modal event listeners
if (settingsButton) {
  settingsButton.addEventListener("click", openSettingsModal);
}

if (settingsCloseButton) {
  settingsCloseButton.addEventListener("click", closeSettingsModal);
}

if (templateEditorButton) {
  templateEditorButton.addEventListener("click", () => {
    void openTemplateEditor();
  });
}

if (templateCloseButton) {
  templateCloseButton.addEventListener("click", closeTemplateEditor);
}

if (templateAddBlockButton) {
  templateAddBlockButton.addEventListener("click", () => {
    const selected = templateBlockSelect?.value || "title";
    addTemplateBlock(selected);
  });
}

if (templateLoadButton) {
  templateLoadButton.addEventListener("click", () => {
    templateLoadInput?.click();
  });
}

if (templateLoadInput) {
  templateLoadInput.addEventListener("change", () => {
    const file = templateLoadInput.files?.[0];
    if (file) {
      loadTemplateFromFile(file);
    }
    templateLoadInput.value = "";
  });
}

if (templateSaveButton) {
  templateSaveButton.addEventListener("click", () => {
    saveTemplateToFile();
  });
}

if (templateResetButton) {
  templateResetButton.addEventListener("click", () => {
    void resetTemplateToDefault();
  });
}

if (templateConfirmYesButton) {
  templateConfirmYesButton.addEventListener("click", () => {
    closeTemplateResetConfirm(true);
  });
}

if (templateConfirmNoButton) {
  templateConfirmNoButton.addEventListener("click", () => {
    closeTemplateResetConfirm(false);
  });
}

if (templateConfirmOverlay) {
  templateConfirmOverlay.addEventListener("click", (event) => {
    if (event.target === templateConfirmOverlay) {
      closeTemplateResetConfirm(false);
    }
  });
}

if (outputConflictOverwriteButton) {
  outputConflictOverwriteButton.addEventListener("click", () => {
    void sendOutputConflictChoice("overwrite");
  });
}

if (outputConflictCopyButton) {
  outputConflictCopyButton.addEventListener("click", () => {
    void sendOutputConflictChoice("copy");
  });
}

if (outputConflictCancelButton) {
  outputConflictCancelButton.addEventListener("click", () => {
    void sendOutputConflictChoice("cancel");
  });
}

if (outputConflictOverlay) {
  outputConflictOverlay.addEventListener("click", (event) => {
    if (event.target === outputConflictOverlay && !outputConflictState.busy) {
      void sendOutputConflictChoice("cancel");
    }
  });
}

if (templateCopyButton) {
  templateCopyButton.addEventListener("click", () => {
    void copyTemplatePreview();
  });
}

if (templateRenderToggle) {
  templateRenderToggle.addEventListener("click", () => {
    templateShowRendered = !templateShowRendered;
    templateRenderToggle.textContent = templateShowRendered ? "BBCode" : "Rendered";
    templateRenderToggle.classList.toggle("active", templateShowRendered);
    renderTemplatePreview();
  });
}

if (skipCompressionToggle) {
  skipCompressionToggle.addEventListener("change", () => {
    settingsState.skipCompression = skipCompressionToggle.checked;
    saveSettings();
  });
}

if (compressionPasswordToggle) {
  compressionPasswordToggle.addEventListener("change", () => {
    settingsState.compressionPasswordEnabled = compressionPasswordToggle.checked;
    syncCompressionPasswordUI(true);
    saveSettings();
  });
}

if (compressionPasswordInput) {
  compressionPasswordInput.addEventListener("input", () => {
    settingsState.compressionPassword = compressionPasswordInput.value;
    saveSettings();
  });
}

if (customCompressionArgsInput) {
  customCompressionArgsInput.addEventListener("input", () => {
    settingsState.customCompressionArgs = customCompressionArgsInput.value;
    saveSettings();
  });
}

if (defaultQrToggle) {
  defaultQrToggle.addEventListener("change", () => {
    settingsState.defaultQrLogin = defaultQrToggle.checked;
    saveSettings();
    applyDefaultQrLogin();
  });
}

if (languageSelect) {
  languageSelect.addEventListener("change", () => {
    settingsState.language = languageSelect.value;
    saveSettings();
    applyTranslations();
    renderAll();
    console.debug("[OmniPacker] Language changed to:", settingsState.language);
  });
}

if (deleteLoginButton) {
  deleteLoginButton.addEventListener("click", () => {
    void deleteSavedLoginDetails();
  });
}

// Load settings from localStorage on startup
loadSettings();
void syncTemplateStorage();
applySettingsToUI();
applyDefaultQrLogin();
void loadSavedLoginDetails();

if (branchInput && !branchInput.value) {
  branchInput.value = "public";
}

// Scale the fixed-size UI to fit the window, preserving proportions.
// Below the design size: use CSS transform to shrink proportionally.
// At or above design size: let the layout flow naturally to fill the space.
const DESIGN_WIDTH = 1140;
const DESIGN_HEIGHT = 760;

// ===== Library tab (owned-app picker) =====
const libraryLoadButton = document.querySelector(".library-load-button");
const libraryRefreshButton = document.querySelector(".library-refresh-button");
const libraryIncludeFree = document.getElementById("library-include-free");
const librarySearch = document.getElementById("library-search");
const libraryListEl = document.getElementById("library-list");
const libraryStatusEl = document.querySelector(".library-status");
const librarySelectionCountEl = document.querySelector(
  ".library-selection-count",
);
const libraryAddButton = document.querySelector(".library-add-button");
const libraryUpdatesEl = document.getElementById("library-updates");
const watchEnabledToggle = document.getElementById("watch-enabled-toggle");
const watchIntervalInput = document.getElementById("watch-interval-input");

const loadWatchConfig = async () => {
  if (!tauriInvoke) return;
  try {
    const cfg = await tauriInvoke("get_watch_config");
    const map = new Map();
    for (const [appid, entry] of Object.entries(cfg?.apps ?? {})) {
      map.set(appid, { autoArchive: Boolean(entry?.autoArchive) });
    }
    libraryState.watched = map;
    if (watchEnabledToggle) watchEnabledToggle.checked = Boolean(cfg?.enabled);
    if (watchIntervalInput && Number.isFinite(cfg?.intervalMinutes)) {
      watchIntervalInput.value = cfg.intervalMinutes;
    }
  } catch (error) {
    console.debug("[OmniPacker] Failed to load watch config:", error);
  }
};

const applyWatchSettings = async () => {
  if (!tauriInvoke) return;
  const interval = Math.max(5, Number(watchIntervalInput?.value) || 60);
  try {
    await tauriInvoke("set_watch_settings", {
      enabled: Boolean(watchEnabledToggle?.checked),
      intervalMinutes: interval,
      username: steamUsernameInput?.value?.trim() || "",
    });
  } catch (error) {
    console.debug("[OmniPacker] Failed to save watch settings:", error);
  }
};

const toggleWatch = async (app) => {
  if (!tauriInvoke) return;
  const key = String(app.appid);
  const enabled = !libraryState.watched.has(key);
  const entry = getSelectionEntry(app.appid);
  try {
    await tauriInvoke("set_app_watch", {
      appid: app.appid,
      name: app.name || `App ${app.appid}`,
      branch: entry.branch || defaultBranchFor(app),
      enabled,
      autoArchive: false,
    });
    if (enabled) libraryState.watched.set(key, { autoArchive: false });
    else libraryState.watched.delete(key);
    renderLibrary();
  } catch (error) {
    setLibraryStatus(`Failed to update watch: ${error}`);
  }
};

const setAutoArchive = async (app, value) => {
  if (!tauriInvoke) return;
  const key = String(app.appid);
  const entry = getSelectionEntry(app.appid);
  try {
    await tauriInvoke("set_app_watch", {
      appid: app.appid,
      name: app.name || `App ${app.appid}`,
      branch: entry.branch || defaultBranchFor(app),
      enabled: true,
      autoArchive: value,
    });
    libraryState.watched.set(key, { autoArchive: value });
  } catch (error) {
    setLibraryStatus(`Failed to update auto-archive: ${error}`);
  }
};

// Enqueue (and start, if idle) archive job(s) for an app that just updated,
// honoring its per-game config (target OS list + branch) when present.
const autoArchiveUpdate = async (update) => {
  let osTargets = [];
  let branch = update.branch || "public";
  try {
    const cfg = await tauriInvoke("get_app_config", { appid: update.appid });
    if (Array.isArray(cfg?.osTargets) && cfg.osTargets.length) {
      osTargets = cfg.osTargets;
    }
    if (cfg?.branch) branch = cfg.branch;
  } catch (error) {
    console.debug("[OmniPacker] app config lookup failed:", error);
  }
  if (osTargets.length === 0) {
    osTargets = [osSelect?.value || "Windows x64"];
  }

  const username = steamUsernameInput?.value?.trim() || "";
  const password = steamPasswordInput?.value || "";
  const qrEnabled = Boolean(qrLoginToggle?.checked);
  for (const os of osTargets) {
    const job = createJob({
      appId: String(update.appid),
      os,
      branch,
      branchPassword: "",
      username,
      password,
      qrEnabled,
    });
    job.status = "queued";
  }
  renderAll();
  if (!jobState.runningJobId) {
    void startJob();
  }
};

const renderUpdatesBanner = () => {
  if (!libraryUpdatesEl) return;
  if (updatesState.items.length === 0) {
    libraryUpdatesEl.hidden = true;
    libraryUpdatesEl.innerHTML = "";
    return;
  }
  libraryUpdatesEl.hidden = false;
  libraryUpdatesEl.innerHTML = "";
  for (const item of updatesState.items) {
    const row = document.createElement("div");
    row.className = "library-updates-row";
    const name = document.createElement("span");
    name.className = "name";
    name.textContent = `⬆ ${item.name || `App ${item.appid}`}`;
    const meta = document.createElement("span");
    meta.textContent = `${item.branch} · build ${item.buildId}`;
    row.appendChild(name);
    row.appendChild(meta);
    libraryUpdatesEl.appendChild(row);
  }
};

const setLibraryStatus = (text) => {
  if (libraryStatusEl) libraryStatusEl.textContent = text || "";
};

const ENUM_LOG_CAP = 2000;
const pushEnumLog = (line) => {
  if (line == null) return;
  enumState.log.push(String(line));
  if (enumState.log.length > ENUM_LOG_CAP) {
    enumState.log.splice(0, enumState.log.length - ENUM_LOG_CAP);
  }
  if (activeTab === "console") {
    scheduleConsoleRender();
  }
};

const formatAgo = (iso) => {
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return "";
  const secs = Math.max(0, Math.floor((Date.now() - t) / 1000));
  if (secs < 60) return "just now";
  const mins = Math.floor(secs / 60);
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  return `${Math.floor(hrs / 24)}d ago`;
};

const getSelectionEntry = (appid) => {
  const key = String(appid);
  let entry = libraryState.selection.get(key);
  if (!entry) {
    entry = { selected: false, branch: "", branchPassword: "" };
    libraryState.selection.set(key, entry);
  }
  return entry;
};

const defaultBranchFor = (app) => {
  if (app.branches?.some((b) => b.name === "public")) return "public";
  return app.branches?.[0]?.name || "public";
};

const filteredApps = () => {
  const q = libraryState.filter.trim().toLowerCase();
  if (!q) return libraryState.apps;
  return libraryState.apps.filter(
    (a) => a.name.toLowerCase().includes(q) || String(a.appid).includes(q),
  );
};

const updateLibrarySelectionUI = () => {
  let count = 0;
  for (const entry of libraryState.selection.values()) {
    if (entry.selected) count += 1;
  }
  if (librarySelectionCountEl) {
    librarySelectionCountEl.textContent = `${count} selected`;
  }
  if (libraryAddButton) {
    libraryAddButton.disabled = count === 0 || isQueueRunning();
  }
};

const renderLibrary = () => {
  if (!libraryListEl) return;
  libraryListEl.innerHTML = "";

  if (libraryState.loading || !libraryState.loaded) {
    const empty = document.createElement("div");
    empty.className = "library-empty";
    empty.textContent = libraryState.loading
      ? "Loading your library…"
      : 'Click "Load My Library" to fetch the apps owned by your signed-in account.';
    libraryListEl.appendChild(empty);
    updateLibrarySelectionUI();
    return;
  }

  const apps = filteredApps();
  if (apps.length === 0) {
    const empty = document.createElement("div");
    empty.className = "library-empty";
    empty.textContent = libraryState.apps.length
      ? "No apps match your search."
      : "No owned apps were found for this account.";
    libraryListEl.appendChild(empty);
    updateLibrarySelectionUI();
    return;
  }

  for (const app of apps) {
    const entry = getSelectionEntry(app.appid);
    if (!entry.branch) entry.branch = defaultBranchFor(app);

    const row = document.createElement("div");
    row.className = "library-item";

    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.checked = entry.selected;
    checkbox.addEventListener("change", () => {
      entry.selected = checkbox.checked;
      updateLibrarySelectionUI();
    });

    const main = document.createElement("div");
    main.className = "library-item-main";
    const name = document.createElement("div");
    name.className = "library-item-name";
    name.textContent = app.name || `App ${app.appid}`;
    const meta = document.createElement("div");
    meta.className = "library-item-meta";
    meta.textContent = `AppID ${app.appid} · ${app.type || "app"}`;
    main.appendChild(name);
    main.appendChild(meta);

    const branchWrap = document.createElement("div");
    branchWrap.className = "library-item-branch";

    const branches =
      app.branches && app.branches.length
        ? app.branches
        : [{ name: "public", pwdRequired: false }];

    const select = document.createElement("select");
    select.className = "library-branch-select";
    for (const branch of branches) {
      const option = document.createElement("option");
      option.value = branch.name;
      option.textContent = branch.pwdRequired
        ? `🔒 ${branch.name}`
        : branch.name;
      if (branch.name === entry.branch) option.selected = true;
      select.appendChild(option);
    }

    const pwInput = document.createElement("input");
    pwInput.type = "password";
    pwInput.className = "library-branch-password";
    pwInput.placeholder = "Branch password";
    pwInput.value = entry.branchPassword || "";

    const branchByName = (n) => branches.find((b) => b.name === n);
    const syncPwVisibility = () => {
      const needs = Boolean(branchByName(entry.branch)?.pwdRequired);
      pwInput.style.display = needs ? "" : "none";
    };

    select.addEventListener("change", () => {
      entry.branch = select.value;
      syncPwVisibility();
    });
    pwInput.addEventListener("input", () => {
      entry.branchPassword = pwInput.value;
    });
    syncPwVisibility();

    branchWrap.appendChild(select);
    branchWrap.appendChild(pwInput);

    const watchInfo = libraryState.watched.get(String(app.appid));
    const watched = Boolean(watchInfo);
    const watchBtn = document.createElement("button");
    watchBtn.type = "button";
    watchBtn.className = `library-watch-toggle${watched ? " watched" : ""}`;
    watchBtn.textContent = watched ? "★" : "☆";
    watchBtn.title = watched
      ? "Watching for updates — click to stop"
      : "Watch this app for updates";
    watchBtn.addEventListener("click", () => void toggleWatch(app));
    branchWrap.appendChild(watchBtn);

    if (watched) {
      const autoLabel = document.createElement("label");
      autoLabel.className = "library-auto-archive";
      autoLabel.title = "Automatically archive this app when it updates";
      const autoCb = document.createElement("input");
      autoCb.type = "checkbox";
      autoCb.checked = Boolean(watchInfo.autoArchive);
      autoCb.addEventListener("change", () => {
        watchInfo.autoArchive = autoCb.checked;
        void setAutoArchive(app, autoCb.checked);
      });
      autoLabel.appendChild(autoCb);
      autoLabel.appendChild(document.createTextNode("auto"));
      branchWrap.appendChild(autoLabel);
    }

    const configBtn = document.createElement("button");
    configBtn.type = "button";
    configBtn.className = "library-config-button";
    configBtn.textContent = "⚙";
    configBtn.title = "Per-game archive config (depots, OS targets)";
    configBtn.addEventListener("click", () => void openAppConfig(app));
    branchWrap.appendChild(configBtn);

    const notesBtn = document.createElement("button");
    notesBtn.type = "button";
    notesBtn.className = "library-notes-button";
    notesBtn.textContent = "📝";
    notesBtn.title = "Patch notes / recent updates";
    notesBtn.addEventListener("click", () => void openPatchNotes(app));
    branchWrap.appendChild(notesBtn);

    row.appendChild(checkbox);
    row.appendChild(main);
    row.appendChild(branchWrap);
    libraryListEl.appendChild(row);
  }

  updateLibrarySelectionUI();
};

const closeAuthModals = () => {
  closeQrModal();
  closeSteamGuardModal();
  closeSteamGuardEmailModal();
};

const loadLibrary = async ({ force = false } = {}) => {
  if (libraryState.loading) return;
  if (!tauriInvoke) {
    setLibraryStatus("Backend unavailable (run inside the app).");
    return;
  }
  if (isQueueRunning()) {
    setLibraryStatus("Cannot load the library while the queue is running.");
    return;
  }

  const username = steamUsernameInput?.value?.trim() || "";
  const password = steamPasswordInput?.value || "";
  const qrEnabled = Boolean(qrLoginToggle?.checked);
  if (!qrEnabled && !username) {
    setLibraryStatus("Enter your Steam username (or enable QR login) first.");
    return;
  }

  libraryState.loading = true;
  setLibraryStatus(force ? "Refreshing from Steam…" : "Loading…");
  renderLibrary();

  enumState.active = true;
  enumState.qrEnabled = qrEnabled;
  enumState.qrCaptureActive = false;
  enumState.qrCaptureLines = [];
  enumState.qrText = null;
  enumState.log = [];
  enumState.showInConsole = true;
  pushEnumLog(
    `--- Library ${force ? "refresh" : "load"} (qr=${qrEnabled}, includeFree=${Boolean(
      libraryIncludeFree?.checked,
    )}) — see Console tab for live output ---`,
  );

  if (libraryLoadButton) libraryLoadButton.disabled = true;
  if (libraryRefreshButton) libraryRefreshButton.disabled = true;

  try {
    const result = await tauriInvoke("enumerate_owned_apps", {
      input: {
        username,
        password,
        qrEnabled,
        includeFree: Boolean(libraryIncludeFree?.checked),
        force,
      },
    });
    const apps = Array.isArray(result?.apps) ? result.apps : [];
    libraryState.apps = apps
      .slice()
      .sort((a, b) => (a.name || "").localeCompare(b.name || ""));
    libraryState.loaded = true;
    libraryState.fetchedAt = result?.fetchedAt || null;
    for (const app of libraryState.apps) {
      const entry = getSelectionEntry(app.appid);
      if (!entry.branch) entry.branch = defaultBranchFor(app);
    }
    const ago = result?.fetchedAt ? formatAgo(result.fetchedAt) : "";
    const source = result?.fromCache
      ? `cached · updated ${ago}${result?.stale ? " · stale — Refresh to update" : ""}`
      : "fetched from Steam";
    setLibraryStatus(`${libraryState.apps.length} apps · ${source}`);
    pushEnumLog(`--- result: ${libraryState.apps.length} apps (${source}) ---`);
  } catch (error) {
    setLibraryStatus(`Failed to load library: ${error}`);
    pushEnumLog(`--- error: ${error} ---`);
  } finally {
    libraryState.loading = false;
    enumState.active = false;
    closeAuthModals();
    if (libraryLoadButton) libraryLoadButton.disabled = false;
    if (libraryRefreshButton) libraryRefreshButton.disabled = false;
    if (librarySearch) librarySearch.disabled = !libraryState.loaded;
    renderLibrary();
  }
};

const addSelectedToQueue = () => {
  const os = osSelect?.value || "Windows x64";
  const username = steamUsernameInput?.value?.trim() || "";
  const password = steamPasswordInput?.value || "";
  const qrEnabled = Boolean(qrLoginToggle?.checked);

  let added = 0;
  for (const app of libraryState.apps) {
    const entry = libraryState.selection.get(String(app.appid));
    if (!entry?.selected) continue;
    const job = createJob({
      appId: String(app.appid),
      os,
      branch: entry.branch || "public",
      branchPassword: entry.branchPassword || "",
      username,
      password,
      qrEnabled,
    });
    job.status = "queued";
    entry.selected = false;
    added += 1;
  }

  if (added > 0) {
    setLibraryStatus(`Added ${added} app(s) to the queue.`);
    setActiveTab("queue");
    renderAll();
  }
  renderLibrary();
};

// ----- Per-game config modal -----
const appConfigOverlay = document.querySelector(".app-config-overlay");
const appConfigTitle = document.querySelector(".app-config-title");
const appConfigBranch = document.getElementById("app-config-branch");
const appConfigOsEl = document.querySelector(".app-config-os");
const appConfigDepotsEl = document.querySelector(".app-config-depots");
const appConfigStatus = document.querySelector(".app-config-status");
const appConfigSaveBtn = document.querySelector(".app-config-save");
const appConfigCloseBtn = document.querySelector(".app-config-close");
const appConfigMetaEl = document.querySelector(".app-config-meta");
let appConfigCurrentApp = null;
let appConfigOsBoxes = {};

const renderAppMeta = (meta) => {
  if (!appConfigMetaEl) return;
  appConfigMetaEl.innerHTML = "";
  if (!meta) return;

  const name = document.createElement("div");
  name.className = "meta-name";
  name.textContent = meta.name || `App ${meta.appid}`;
  appConfigMetaEl.appendChild(name);

  const addRow = (k, v) => {
    if (!v) return;
    const row = document.createElement("div");
    row.className = "meta-row";
    const kk = document.createElement("span");
    kk.className = "k";
    kk.textContent = k;
    const vv = document.createElement("span");
    vv.textContent = v;
    row.appendChild(kk);
    row.appendChild(vv);
    appConfigMetaEl.appendChild(row);
  };

  addRow("Type", [meta.appType, meta.isFree ? "free" : null].filter(Boolean).join(" · "));
  addRow(
    "Released",
    meta.comingSoon ? `${meta.releaseDate || "TBA"} (coming soon)` : meta.releaseDate,
  );
  addRow("Developer", (meta.developers || []).join(", "));
  addRow("Publisher", (meta.publishers || []).join(", "));
  addRow("Genres", (meta.genres || []).join(", "));
  addRow(
    "Platforms",
    [meta.windows ? "Windows" : null, meta.mac ? "macOS" : null, meta.linux ? "Linux" : null]
      .filter(Boolean)
      .join(", "),
  );
  if (meta.languages) {
    addRow(
      "Languages",
      meta.languages.length > 120 ? `${meta.languages.slice(0, 120)}…` : meta.languages,
    );
  }

  const badges = document.createElement("div");
  badges.className = "meta-badges";
  const addBadge = (text) => {
    if (!text) return;
    const b = document.createElement("span");
    b.className = "badge";
    b.textContent = text;
    badges.appendChild(b);
  };
  if (meta.reviewDesc) {
    const extra =
      meta.positivePercent != null
        ? ` (${meta.positivePercent}%${meta.totalReviews ? `, ${meta.totalReviews.toLocaleString()}` : ""})`
        : "";
    addBadge(`${meta.reviewDesc}${extra}`);
  }
  if (meta.playerCount != null) addBadge(`${meta.playerCount.toLocaleString()} playing`);
  if (meta.metacritic != null) addBadge(`Metacritic ${meta.metacritic}`);
  if (meta.dlcCount) addBadge(`${meta.dlcCount} DLC`);
  if (badges.children.length) appConfigMetaEl.appendChild(badges);
};

const humanSize = (bytes) => {
  const n = Number(bytes);
  if (!Number.isFinite(n) || n <= 0) return "";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let v = n;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i += 1;
  }
  return `${v.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
};

const osTargetValues = () =>
  Array.from(osSelect?.options ?? []).map((o) => o.value);

const renderConfigDepots = (depots) => {
  if (!appConfigDepotsEl) return;
  appConfigDepotsEl.innerHTML = "";
  if (!depots || depots.length === 0) {
    appConfigDepotsEl.textContent = "No depots reported.";
    return;
  }
  for (const d of depots) {
    const row = document.createElement("div");
    row.className = "app-config-depot";
    const left = document.createElement("span");
    left.textContent = `${d.depotId}${d.name ? ` · ${d.name}` : ""}`;
    const right = document.createElement("span");
    right.className = "meta";
    const parts = [];
    if (d.oslist) parts.push(d.oslist);
    const size = humanSize(d.size);
    if (size) parts.push(size);
    right.textContent = parts.join(" · ");
    row.appendChild(left);
    row.appendChild(right);
    appConfigDepotsEl.appendChild(row);
  }
};

const closeAppConfig = () => {
  appConfigOverlay?.classList.remove("active");
  appConfigCurrentApp = null;
};

const openAppConfig = async (app) => {
  if (!tauriInvoke) return;
  appConfigCurrentApp = app;
  if (appConfigTitle) {
    appConfigTitle.textContent = `Config — ${app.name || `App ${app.appid}`}`;
  }
  if (appConfigStatus) appConfigStatus.textContent = "Loading depots…";
  if (appConfigDepotsEl) appConfigDepotsEl.innerHTML = "";
  if (appConfigMetaEl) appConfigMetaEl.innerHTML = "";

  // Rich metadata (developer, reviews, players, …) from public Steam endpoints.
  tauriInvoke("get_app_meta", { appid: app.appid })
    .then((meta) => renderAppMeta(meta))
    .catch((error) => console.debug("[OmniPacker] app meta failed:", error));

  if (appConfigBranch) {
    appConfigBranch.innerHTML = "";
    const def = document.createElement("option");
    def.value = "";
    def.textContent = "(default / public)";
    appConfigBranch.appendChild(def);
    for (const b of app.branches || []) {
      const o = document.createElement("option");
      o.value = b.name;
      o.textContent = b.pwdRequired ? `🔒 ${b.name}` : b.name;
      appConfigBranch.appendChild(o);
    }
  }

  appConfigOsBoxes = {};
  if (appConfigOsEl) {
    appConfigOsEl.innerHTML = "";
    for (const os of osTargetValues()) {
      const label = document.createElement("label");
      const cb = document.createElement("input");
      cb.type = "checkbox";
      label.appendChild(cb);
      label.appendChild(document.createTextNode(os));
      appConfigOsEl.appendChild(label);
      appConfigOsBoxes[os] = cb;
    }
  }

  appConfigOverlay?.classList.add("active");

  try {
    const cfg = await tauriInvoke("get_app_config", { appid: app.appid });
    if (appConfigBranch && cfg?.branch) appConfigBranch.value = cfg.branch;
    for (const os of cfg?.osTargets || []) {
      if (appConfigOsBoxes[os]) appConfigOsBoxes[os].checked = true;
    }
  } catch (error) {
    console.debug("[OmniPacker] config load failed:", error);
  }

  try {
    const details = await tauriInvoke("get_app_detail", {
      input: {
        appids: [app.appid],
        username: steamUsernameInput?.value?.trim() || "",
      },
    });
    const detail = Array.isArray(details) ? details[0] : null;
    renderConfigDepots(detail?.depots || []);
    if (appConfigStatus) {
      appConfigStatus.textContent = `${(detail?.depots || []).length} depots`;
    }
  } catch (error) {
    if (appConfigStatus) {
      appConfigStatus.textContent = `Couldn't load depots: ${error}`;
    }
  }
};

const saveAppConfig = async () => {
  if (!tauriInvoke || !appConfigCurrentApp) return;
  const osTargets = osTargetValues().filter((os) => appConfigOsBoxes[os]?.checked);
  try {
    await tauriInvoke("set_app_config", {
      appid: appConfigCurrentApp.appid,
      config: { branch: appConfigBranch?.value || "", osTargets },
    });
    closeAppConfig();
  } catch (error) {
    if (appConfigStatus) appConfigStatus.textContent = `Save failed: ${error}`;
  }
};

if (appConfigSaveBtn) {
  appConfigSaveBtn.addEventListener("click", () => void saveAppConfig());
}
if (appConfigCloseBtn) appConfigCloseBtn.addEventListener("click", closeAppConfig);
if (appConfigOverlay) {
  appConfigOverlay.addEventListener("click", (e) => {
    if (e.target === appConfigOverlay) closeAppConfig();
  });
}

// ----- Patch notes modal -----
const patchNotesOverlay = document.querySelector(".patch-notes-overlay");
const patchNotesTitle = document.querySelector(".patch-notes-title");
const patchNotesList = document.querySelector(".patch-notes-list");
const patchNotesContent = document.querySelector(".patch-notes-content");
const patchNotesStatus = document.querySelector(".patch-notes-status");
const patchNotesCloseBtn = document.querySelector(".patch-notes-close");

const escapeHtml = (s) =>
  String(s ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");

// Minimal Steam BBCode -> safe HTML (input is escaped first, only known tags
// are reintroduced, external images become labels since the CSP blocks them).
// Removes BBCode tags from a fragment (used for spoiler titles etc.).
const stripBbTags = (s) =>
  String(s ?? "")
    .replace(/\[\/?[a-z0-9*][^\]]*\]/gi, "")
    .replace(/^"+|"+$/g, "")
    .trim();

const bbcodeToHtml = (raw) => {
  let s = escapeHtml(raw);
  s = s.replace(/\[h1\]([\s\S]*?)\[\/h1\]/gi, "<h3>$1</h3>");
  s = s.replace(/\[h2\]([\s\S]*?)\[\/h2\]/gi, "<h4>$1</h4>");
  s = s.replace(/\[h3\]([\s\S]*?)\[\/h3\]/gi, "<h5>$1</h5>");
  s = s.replace(/\[b\]([\s\S]*?)\[\/b\]/gi, "<strong>$1</strong>");
  s = s.replace(/\[i\]([\s\S]*?)\[\/i\]/gi, "<em>$1</em>");
  s = s.replace(/\[u\]([\s\S]*?)\[\/u\]/gi, "<u>$1</u>");
  s = s.replace(/\[(?:s|strike)\]([\s\S]*?)\[\/(?:s|strike)\]/gi, "<s>$1</s>");
  s = s.replace(/\[quote[^\]]*\]([\s\S]*?)\[\/quote\]/gi, "<blockquote>$1</blockquote>");
  // Code (with optional =lang) -> run code before spoiler so nested code wraps.
  s = s.replace(/\[code(?:=[^\]]*)?\]([\s\S]*?)\[\/code\]/gi, "<pre>$1</pre>");
  // Spoiler (with optional =title) -> labeled block.
  s = s.replace(
    /\[spoiler(?:=([^\]]*))?\]([\s\S]*?)\[\/spoiler\]/gi,
    (_m, title, body) => {
      const label = title ? stripBbTags(title) : "Spoiler";
      return `<div class="bb-spoiler"><div class="bb-spoiler-title">${label}</div>${body}</div>`;
    },
  );
  s = s.replace(
    /\[url=([^\]]+)\]([\s\S]*?)\[\/url\]/gi,
    (_m, href, txt) => `<span class="bb-link" title="${href.replace(/"/g, "&quot;")}">${txt}</span>`,
  );
  s = s.replace(/\[url\]([\s\S]*?)\[\/url\]/gi, (_m, href) => `<span class="bb-link">${href}</span>`);
  s = s.replace(
    /\[img\]([\s\S]*?)\[\/img\]/gi,
    (_m, src) => `<span class="bb-link" title="${src.replace(/"/g, "&quot;")}">[image]</span>`,
  );
  // List items: Steam closes each with [/*].
  s = s.replace(/\[\/\*\]/gi, "");
  s = s.replace(/\[\*\]\s*/gi, "<br>• ");
  // Strip only KNOWN formatting tags (with optional =value). Crucially this
  // leaves literal bracketed text like [Win64], [Branch: Public] or
  // [Manifest 123] intact instead of mistaking them for tags.
  const KNOWN_TAGS =
    "b|i|u|s|strike|color|size|list|olist|ulist|quote|code|spoiler|center|left|right|table|tr|td|th|h[1-6]|noparse|sub|sup|previewyoutube|steam|hr";
  s = s.replace(
    new RegExp(`\\[\\/?(?:${KNOWN_TAGS})(?:=[^\\]]*)?\\]`, "gi"),
    "",
  );
  s = s.replace(/\r?\n/g, "<br>");
  s = s.replace(/(<br>\s*){3,}/g, "<br><br>");
  return s;
};

const fmtNewsDate = (epochSecs) => {
  const t = Number(epochSecs) * 1000;
  if (!Number.isFinite(t) || t <= 0) return "";
  return new Date(t).toLocaleDateString();
};

const showPatchNote = (item, btn) => {
  patchNotesList
    ?.querySelectorAll(".patch-notes-item.active")
    .forEach((e) => e.classList.remove("active"));
  if (btn) btn.classList.add("active");
  if (patchNotesContent) {
    patchNotesContent.innerHTML = item ? bbcodeToHtml(item.contents) : "";
    patchNotesContent.scrollTop = 0;
  }
};

const openPatchNotes = async (app) => {
  if (!tauriInvoke) return;
  if (patchNotesTitle) {
    patchNotesTitle.textContent = `Patch Notes — ${app.name || `App ${app.appid}`}`;
  }
  if (patchNotesList) patchNotesList.innerHTML = "";
  if (patchNotesContent) patchNotesContent.innerHTML = "";
  if (patchNotesStatus) patchNotesStatus.textContent = "Loading…";
  patchNotesOverlay?.classList.add("active");

  try {
    const result = await tauriInvoke("get_app_news", { appid: app.appid, count: 15 });
    const items = result?.items || [];
    if (items.length === 0) {
      if (patchNotesStatus) patchNotesStatus.textContent = "No news found for this app.";
      return;
    }
    items.forEach((item, idx) => {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "patch-notes-item";
      const title = document.createElement("span");
      title.textContent = item.title || "(untitled)";
      const date = document.createElement("span");
      date.className = "date";
      date.textContent = [fmtNewsDate(item.date), item.feedlabel]
        .filter(Boolean)
        .join(" · ");
      btn.appendChild(title);
      btn.appendChild(date);
      btn.addEventListener("click", () => showPatchNote(item, btn));
      patchNotesList?.appendChild(btn);
      if (idx === 0) showPatchNote(item, btn);
    });
    if (patchNotesStatus) {
      patchNotesStatus.textContent = `${items.length} items · ${result?.fromCache ? "cached" : "fetched"}`;
    }
  } catch (error) {
    if (patchNotesStatus) patchNotesStatus.textContent = `Failed to load news: ${error}`;
  }
};

const closePatchNotes = () => patchNotesOverlay?.classList.remove("active");
if (patchNotesCloseBtn) patchNotesCloseBtn.addEventListener("click", closePatchNotes);
if (patchNotesOverlay) {
  patchNotesOverlay.addEventListener("click", (e) => {
    if (e.target === patchNotesOverlay) closePatchNotes();
  });
}

if (libraryLoadButton) {
  libraryLoadButton.addEventListener("click", () => void loadLibrary());
}
if (libraryRefreshButton) {
  libraryRefreshButton.addEventListener("click", () =>
    void loadLibrary({ force: true }),
  );
}
if (librarySearch) {
  librarySearch.addEventListener("input", () => {
    libraryState.filter = librarySearch.value;
    renderLibrary();
  });
}
if (libraryAddButton) {
  libraryAddButton.addEventListener("click", addSelectedToQueue);
}
if (watchEnabledToggle) {
  watchEnabledToggle.addEventListener("change", () => void applyWatchSettings());
}
if (watchIntervalInput) {
  watchIntervalInput.addEventListener("change", () => void applyWatchSettings());
}

// Enumeration login events: reuse the QR / Steam Guard modals via the enum
// pseudo-job, and surface progress in the library status line.
if (tauriEvent?.listen) {
  tauriEvent.listen("enum:log", (event) => {
    if (!enumState.active) return;
    const payload = event.payload ?? {};
    const line = payload.line ?? "";

    pushEnumLog(`[${payload.stream || "dd"}] ${line}`);

    // No password field exists in the Library flow; if DepotDownloader falls back
    // to prompting for one on stdin, abort instead of hanging forever.
    if (line.includes("Enter account password for")) {
      setLibraryStatus(
        "Enter your Steam password (or enable QR login) and load again.",
      );
      void tauriInvoke?.("cancel_enumeration");
      return;
    }

    updateQrCapture(enumState, payload);
    if (enumState.qrEnabled && qrLoginSuccessMatches(line)) {
      closeQrModal();
    }
    if (steamGuardPromptMatches(line)) {
      openSteamGuardModal();
    }
    if (steamGuardConfirmedMatches(line)) {
      closeSteamGuardModal();
    }
    if (steamGuardEmailPromptMatches(line)) {
      openSteamGuardEmailModal(line, null);
    }
  });

  tauriEvent.listen("enum:progress", (event) => {
    if (!enumState.active) return;
    const message = event.payload?.message ?? "";
    if (message) {
      setLibraryStatus(`Working… ${message}`);
      pushEnumLog(`[progress] ${message}`);
    }
  });

  tauriEvent.listen("update:available", (event) => {
    const u = event.payload ?? {};
    // De-dupe by appid+buildId so repeated polls don't stack entries.
    const exists = updatesState.items.some(
      (i) => i.appid === u.appid && i.buildId === u.buildId,
    );
    if (!exists) {
      updatesState.items.unshift(u);
      renderUpdatesBanner();
      if (u.autoArchive) {
        void autoArchiveUpdate(u);
      }
    }
  });

  tauriEvent.listen("watch:error", (event) => {
    console.debug("[OmniPacker] watch error:", event.payload);
  });
}

renderLibrary();
renderUpdatesBanner();
void loadWatchConfig();

function applyUiScale() {
  const scale = Math.min(
    window.innerWidth / DESIGN_WIDTH,
    window.innerHeight / DESIGN_HEIGHT,
  );

  const body = document.body;
  if (scale < 1) {
    // Small window: fixed size + transform scale down
    body.classList.add("fixed-layout");
    body.classList.remove("fluid-layout");
    document.documentElement.style.setProperty("--ui-scale", String(scale));
  } else {
    // Large window: flow naturally
    body.classList.remove("fixed-layout");
    body.classList.add("fluid-layout");
    document.documentElement.style.setProperty("--ui-scale", "1");
  }
}

window.addEventListener("resize", applyUiScale);
applyUiScale();

renderAll();
