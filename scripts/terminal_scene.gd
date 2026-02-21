extends Control

@export var max_scrollback_lines: int = 1000
@export var terminal_background_color: Color = Color(0.0470588, 0.0588235, 0.0705882, 1.0)
@export var terminal_text_color: Color = Color(0.839216, 0.976471, 0.858824, 1.0)
@export var terminal_caret_color: Color = Color(0.545098, 0.952941, 0.639216, 1.0)
@export var terminal_selection_color: Color = Color(0.22, 0.35, 0.24, 1.0)
@export var terminal_font_size: int = 18
@export var event_poll_interval_seconds: float = 0.10

var text_buffer: Array[String] = []
var world_runtime: Node = null
var current_node_id: String = ""
var current_user_key: String = ""
var current_cwd: String = "/"
var prompt_user: String = "player"
var prompt_host: String = "term"
var event_poll_elapsed: float = 0.0

@onready var background: ColorRect = $Background
@onready var terminal_vbox: VBoxContainer = $VBox
@onready var output_scroll: ScrollContainer = $VBox/OutputScroll
@onready var output_label: RichTextLabel = $VBox/OutputScroll/Output
@onready var prompt_label: Label = $VBox/InputRow/Prompt
@onready var input_line: LineEdit = $VBox/InputRow/Input
@onready var editor_overlay: Control = $EditorOverlay
@onready var editor: CodeEdit = $EditorOverlay/Editor


func _ready() -> void:
	input_line.keep_editing_on_text_submit = true
	input_line.text_submitted.connect(_on_input_submitted)
	editor.gui_input.connect(_on_editor_gui_input)
	editor.shortcut_keys_enabled = true
	_apply_terminal_theme()
	_initialize_runtime_bridge()
	_refresh_prompt()
	input_line.grab_focus()
	_append_output("Terminal prototype ready.")


func _unhandled_input(event: InputEvent) -> void:
	if editor_overlay.visible:
		return
	if event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		input_line.grab_focus()


func _process(delta: float) -> void:
	if world_runtime == null:
		return
	if not world_runtime.has_method("DrainTerminalEventLines"):
		return

	event_poll_elapsed += delta
	var interval := maxf(0.01, event_poll_interval_seconds)
	if event_poll_elapsed < interval:
		return

	event_poll_elapsed = 0.0
	var lines_variant: Variant = world_runtime.call("DrainTerminalEventLines", current_node_id, current_user_key)
	if lines_variant is Array:
		var lines_array: Array = lines_variant
		for line in lines_array:
			_append_output(str(line))


func _on_input_submitted(command_text: String) -> void:
	var trimmed: String = command_text.strip_edges()
	if trimmed.is_empty():
		input_line.clear()
		input_line.call_deferred("grab_focus")
		return

	_append_command_echo(trimmed)

	if world_runtime == null:
		_append_output("error: world runtime singleton '/root/WorldRuntime' not found.")
		input_line.clear()
		input_line.call_deferred("grab_focus")
		return

	if not world_runtime.has_method("ExecuteTerminalCommand"):
		_append_output("error: runtime bridge method not found: ExecuteTerminalCommand.")
		input_line.clear()
		input_line.call_deferred("grab_focus")
		return

	var response: Dictionary = world_runtime.call(
		"ExecuteTerminalCommand",
		current_node_id,
		current_user_key,
		current_cwd,
		trimmed)
	_apply_systemcall_response(response)

	input_line.clear()
	input_line.call_deferred("grab_focus")


func _append_command_echo(command_text: String) -> void:
	if text_buffer.size() > 0 and text_buffer[text_buffer.size() - 1] != "":
		_append_output("")

	_append_output("%s%s" % [prompt_label.text, command_text])


func _append_output(line: String) -> void:
	text_buffer.append(line)
	if text_buffer.size() > max_scrollback_lines:
		text_buffer = text_buffer.slice(text_buffer.size() - max_scrollback_lines, text_buffer.size())
	output_label.text = "\n".join(text_buffer)
	call_deferred("_scroll_to_bottom")


func _scroll_to_bottom() -> void:
	var v_scroll := output_scroll.get_v_scroll_bar()
	if v_scroll:
		output_scroll.scroll_vertical = int(v_scroll.max_value)


func _apply_terminal_theme() -> void:
	background.color = terminal_background_color

	output_label.add_theme_color_override("default_color", terminal_text_color)
	prompt_label.add_theme_color_override("font_color", terminal_text_color)
	input_line.add_theme_color_override("font_color", terminal_text_color)
	input_line.add_theme_color_override("caret_color", terminal_caret_color)
	input_line.add_theme_color_override("selection_color", terminal_selection_color)

	editor.add_theme_color_override("font_color", terminal_text_color)
	editor.add_theme_color_override("caret_color", terminal_caret_color)
	editor.add_theme_color_override("selection_color", terminal_selection_color)
	editor.add_theme_color_override("background_color", terminal_background_color)

	output_label.add_theme_font_size_override("font_size", terminal_font_size)
	prompt_label.add_theme_font_size_override("font_size", terminal_font_size)
	input_line.add_theme_font_size_override("font_size", terminal_font_size)
	editor.add_theme_font_size_override("font_size", terminal_font_size)

	var shared_font: Font = input_line.get_theme_font("font")
	if shared_font:
		output_label.add_theme_font_override("font", shared_font)
		prompt_label.add_theme_font_override("font", shared_font)
		input_line.add_theme_font_override("font", shared_font)
		editor.add_theme_font_override("font", shared_font)


func _enter_editor_mode() -> void:
	editor_overlay.visible = true
	terminal_vbox.visible = false
	input_line.editable = false
	editor.grab_focus()


func _exit_editor_mode() -> void:
	editor_overlay.visible = false
	terminal_vbox.visible = true
	input_line.editable = true
	_append_output(_to_oneline_string(editor.text))
	input_line.call_deferred("grab_focus")


func _to_oneline_string(multiline_text: String) -> String:
	var normalized: String = multiline_text.replace("\r\n", "\n").replace("\r", "\n")
	return normalized.replace("\n", "\\n")


func _on_editor_gui_input(event: InputEvent) -> void:
	if not editor_overlay.visible:
		return
	if event is not InputEventKey:
		return

	var key_event := event as InputEventKey
	if not key_event.pressed or key_event.echo:
		return

	if key_event.keycode == KEY_ESCAPE:
		_exit_editor_mode()
		editor.accept_event()
		return

	if key_event.ctrl_pressed and key_event.keycode == KEY_S:
		_exit_editor_mode()
		editor.accept_event()


func _initialize_runtime_bridge() -> void:
	world_runtime = get_node_or_null("/root/WorldRuntime")
	if world_runtime == null:
		_append_output("error: world runtime singleton '/root/WorldRuntime' not found.")
		return

	if not world_runtime.has_method("GetDefaultTerminalContext"):
		_append_output("error: runtime bridge method not found: GetDefaultTerminalContext.")
		world_runtime = null
		return

	var context: Dictionary = world_runtime.call("GetDefaultTerminalContext", "player")
	if not bool(context.get("ok", false)):
		_append_output(str(context.get("error", "error: failed to initialize terminal context.")))
		world_runtime = null
		return

	current_node_id = str(context.get("nodeId", ""))
	current_user_key = str(context.get("userKey", ""))
	current_cwd = str(context.get("cwd", "/"))
	prompt_user = str(context.get("promptUser", current_user_key))
	prompt_host = str(context.get("promptHost", current_node_id))


func _apply_systemcall_response(response: Dictionary) -> void:
	var lines_variant: Variant = response.get("lines", [])
	if lines_variant is Array:
		var lines_array: Array = lines_variant
		for line in lines_array:
			_append_output(str(line))

	var next_cwd: String = str(response.get("nextCwd", ""))
	if not next_cwd.is_empty():
		current_cwd = next_cwd

	_refresh_prompt()


func _refresh_prompt() -> void:
	var display_cwd := current_cwd
	if display_cwd.is_empty():
		display_cwd = "/"

	if display_cwd.begins_with("/home/" + prompt_user):
		var suffix := display_cwd.substr(("/home/" + prompt_user).length())
		display_cwd = "~" + suffix

	prompt_label.text = "%s@%s:%s $ " % [prompt_user, prompt_host, display_cwd]
