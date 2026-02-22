extends Control

@export var max_scrollback_lines: int = 1000
@export var terminal_background_color: Color = Color(0.0470588, 0.0588235, 0.0705882, 1.0)
@export var terminal_text_color: Color = Color(0.839216, 0.976471, 0.858824, 1.0)
@export var terminal_caret_color: Color = Color(0.545098, 0.952941, 0.639216, 1.0)
@export var terminal_selection_color: Color = Color(0.22, 0.35, 0.24, 1.0)
@export var terminal_font_size: int = 18
@export var event_poll_interval_seconds: float = 0.10

const EDITOR_HELP_TEXT := "Ctrl+S: save | Esc: exit"
const EDITOR_STATUS_TIMEOUT_SECONDS: float = 3.0
const OUTPUT_SCROLL_MODE_NORMAL_BOTTOM := "normal_bottom"
const OUTPUT_SCROLL_MODE_MOTD_ANCHOR_ACTIVATE := "motd_anchor_activate"
const OUTPUT_SCROLL_MODE_MOTD_ANCHOR_CONTINUE := "motd_anchor_continue"

var text_buffer: Array[String] = []
var world_runtime: Node = null
var current_node_id: String = ""
var current_user_id: String = ""
var current_cwd: String = "/"
var current_terminal_session_id: String = ""
var prompt_user: String = "player"
var prompt_host: String = "term"
var event_poll_elapsed: float = 0.0
var current_editor_path: String = ""
var current_editor_read_only: bool = false
var current_editor_display_mode: String = "text"
var current_editor_path_exists: bool = false
var editor_status_revision: int = 0
var is_program_running: bool = false
var is_motd_anchor_active: bool = false
var motd_anchor_blank_px: float = 0.0
var motd_anchor_base_content_px: float = 0.0
var is_programmatic_scroll_change: bool = false
var has_pending_scroll_settle: bool = false
var has_pending_anchor_overflow_check: bool = false

@onready var background: ColorRect = $Background
@onready var terminal_vbox: VBoxContainer = $VBox
@onready var output_scroll: ScrollContainer = $VBox/OutputScroll
@onready var output_label: RichTextLabel = $VBox/OutputScroll/OutputContent/Output
@onready var bottom_spacer: Control = $VBox/OutputScroll/OutputContent/BottomSpacer
@onready var prompt_label: Label = $VBox/InputRow/Prompt
@onready var input_line: LineEdit = $VBox/InputRow/Input
@onready var editor_overlay: Control = $EditorOverlay
@onready var editor: CodeEdit = $EditorOverlay/EditorLayout/Editor
@onready var editor_status_label: Label = $EditorOverlay/EditorLayout/EditorStatus


func _ready() -> void:
	input_line.keep_editing_on_text_submit = true
	input_line.text_submitted.connect(_on_input_submitted)
	editor.gui_input.connect(_on_editor_gui_input)
	editor.shortcut_keys_enabled = true
	var v_scroll := output_scroll.get_v_scroll_bar()
	if v_scroll:
		v_scroll.value_changed.connect(_on_output_scroll_value_changed)
	_apply_terminal_theme()
	_initialize_runtime_bridge()
	_refresh_prompt()
	input_line.grab_focus()


func _input(event: InputEvent) -> void:
	if editor_overlay.visible or not is_program_running:
		return
	if event is not InputEventKey:
		return

	var key_event := event as InputEventKey
	if not key_event.pressed or key_event.echo:
		return
	if key_event.ctrl_pressed and key_event.keycode == KEY_C:
		_request_program_interrupt()
		get_viewport().set_input_as_handled()


func _unhandled_input(event: InputEvent) -> void:
	if editor_overlay.visible:
		return
	if event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		input_line.grab_focus()


func _process(delta: float) -> void:
	if world_runtime == null:
		return
	if is_program_running and world_runtime.has_method("IsTerminalProgramRunning"):
		var running_variant: Variant = world_runtime.call("IsTerminalProgramRunning", current_terminal_session_id)
		is_program_running = bool(running_variant)
	if not world_runtime.has_method("DrainTerminalEventLines"):
		return

	event_poll_elapsed += delta
	var interval := maxf(0.01, event_poll_interval_seconds)
	if event_poll_elapsed < interval:
		return

	event_poll_elapsed = 0.0
	var lines_variant: Variant = world_runtime.call("DrainTerminalEventLines", current_node_id, current_user_id)
	var lines := _variant_lines_to_string_array(lines_variant)
	_append_output_batch(lines)


func _on_input_submitted(command_text: String) -> void:
	if is_program_running:
		return

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

	if world_runtime.has_method("TryStartTerminalProgramExecution"):
		var async_variant: Variant = world_runtime.call(
			"TryStartTerminalProgramExecution",
			current_node_id,
			current_user_id,
			current_cwd,
			trimmed,
			current_terminal_session_id)
		if async_variant is Dictionary:
			var async_start: Dictionary = async_variant
			var handled_async: bool = bool(async_start.get("handled", false))
			if handled_async:
				var async_response_variant: Variant = async_start.get("response", {})
				if async_response_variant is Dictionary:
					var async_response: Dictionary = async_response_variant
					_apply_systemcall_response(async_response)

				is_program_running = bool(async_start.get("started", false))
				input_line.clear()
				if not editor_overlay.visible:
					input_line.call_deferred("grab_focus")
				return

	var response: Dictionary = world_runtime.call(
		"ExecuteTerminalCommand",
		current_node_id,
		current_user_id,
		current_cwd,
		trimmed,
		current_terminal_session_id)
	_apply_systemcall_response(response)

	input_line.clear()
	if not editor_overlay.visible:
		input_line.call_deferred("grab_focus")


func _append_command_echo(command_text: String) -> void:
	var lines: Array[String] = []
	if text_buffer.size() > 0 and text_buffer[text_buffer.size() - 1] != "":
		lines.append("")

	lines.append("%s%s" % [prompt_label.text, command_text])
	_append_output_batch(lines)


func _append_output(line: String, mode: String = "") -> void:
	var lines: Array[String] = []
	lines.append(line)
	_append_output_batch(lines, mode)


func _append_output_batch(lines: Array[String], mode: String = "") -> void:
	if lines.is_empty():
		return

	var resolved_mode := mode
	if resolved_mode.is_empty():
		resolved_mode = _resolve_default_output_mode()

	var content_before := _get_output_content_height()
	for line in lines:
		text_buffer.append(line)
	if text_buffer.size() > max_scrollback_lines:
		text_buffer = text_buffer.slice(text_buffer.size() - max_scrollback_lines, text_buffer.size())
	output_label.text = "\n".join(text_buffer)
	call_deferred("_post_append_output", resolved_mode, content_before)


func _post_append_output(mode: String, content_before: float) -> void:
	if mode == OUTPUT_SCROLL_MODE_MOTD_ANCHOR_ACTIVATE:
		_activate_motd_anchor(content_before)
		return

	_apply_default_scroll_policy_after_append()


func _apply_default_scroll_policy_after_append() -> void:
	if is_motd_anchor_active:
		var grown_px := maxf(0.0, _get_output_content_height() - motd_anchor_base_content_px)
		if grown_px > motd_anchor_blank_px:
			_dismiss_motd_anchor()
			_scroll_to_bottom()
		else:
			_schedule_anchor_overflow_check()
		return

	_scroll_to_bottom()


func _activate_motd_anchor(content_before: float) -> void:
	var content_after := _get_output_content_height()
	var motd_height_px := maxf(0.0, content_after - content_before)
	var viewport_height_px := output_scroll.size.y
	if viewport_height_px <= 0.0:
		call_deferred("_activate_motd_anchor", content_before)
		return

	var blank_px := maxf(0.0, viewport_height_px - motd_height_px)
	is_programmatic_scroll_change = true
	_set_bottom_spacer_height(blank_px)

	is_motd_anchor_active = blank_px > 0.0
	motd_anchor_blank_px = blank_px
	motd_anchor_base_content_px = content_after
	has_pending_anchor_overflow_check = false
	_scroll_to_bottom()


func _dismiss_motd_anchor() -> void:
	is_programmatic_scroll_change = true
	_set_bottom_spacer_height(0.0)
	call_deferred("_clear_programmatic_scroll_change")
	is_motd_anchor_active = false
	motd_anchor_blank_px = 0.0
	motd_anchor_base_content_px = 0.0
	has_pending_anchor_overflow_check = false


func _set_bottom_spacer_height(height_px: float) -> void:
	var spacer_size := bottom_spacer.custom_minimum_size
	bottom_spacer.custom_minimum_size = Vector2(spacer_size.x, maxf(0.0, height_px))


func _schedule_anchor_overflow_check() -> void:
	if has_pending_anchor_overflow_check:
		return
	has_pending_anchor_overflow_check = true
	var overflow_timer := get_tree().create_timer(0.0)
	overflow_timer.timeout.connect(_on_anchor_overflow_check_timeout)


func _on_anchor_overflow_check_timeout() -> void:
	has_pending_anchor_overflow_check = false
	if not is_motd_anchor_active:
		return

	var grown_px := maxf(0.0, _get_output_content_height() - motd_anchor_base_content_px)
	if grown_px <= motd_anchor_blank_px:
		return

	_dismiss_motd_anchor()
	_scroll_to_bottom()


func _resolve_default_output_mode() -> String:
	if is_motd_anchor_active:
		return OUTPUT_SCROLL_MODE_MOTD_ANCHOR_CONTINUE
	return OUTPUT_SCROLL_MODE_NORMAL_BOTTOM


func _get_output_content_height() -> float:
	return float(output_label.get_content_height())


func _scroll_to_bottom() -> void:
	if not _set_scroll_to_max():
		return

	is_programmatic_scroll_change = true
	call_deferred("_set_scroll_to_max")

	if has_pending_scroll_settle:
		return

	has_pending_scroll_settle = true
	var settle_timer := get_tree().create_timer(0.0)
	settle_timer.timeout.connect(_on_scroll_settle_timeout)


func _set_scroll_to_max() -> bool:
	var v_scroll := output_scroll.get_v_scroll_bar()
	if v_scroll == null:
		return false

	output_scroll.scroll_vertical = int(v_scroll.max_value)
	return true


func _on_scroll_settle_timeout() -> void:
	has_pending_scroll_settle = false
	_set_scroll_to_max()
	call_deferred("_clear_programmatic_scroll_change")


func _clear_programmatic_scroll_change() -> void:
	is_programmatic_scroll_change = false


func _on_output_scroll_value_changed(_value: float) -> void:
	if is_programmatic_scroll_change:
		return
	if is_motd_anchor_active:
		_dismiss_motd_anchor()


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
	editor_status_label.add_theme_color_override("font_color", terminal_text_color)

	output_label.add_theme_font_size_override("font_size", terminal_font_size)
	prompt_label.add_theme_font_size_override("font_size", terminal_font_size)
	input_line.add_theme_font_size_override("font_size", terminal_font_size)
	editor.add_theme_font_size_override("font_size", terminal_font_size)
	editor_status_label.add_theme_font_size_override("font_size", terminal_font_size)

	var shared_font: Font = input_line.get_theme_font("font")
	if shared_font:
		output_label.add_theme_font_override("font", shared_font)
		prompt_label.add_theme_font_override("font", shared_font)
		input_line.add_theme_font_override("font", shared_font)
		editor.add_theme_font_override("font", shared_font)
		editor_status_label.add_theme_font_override("font", shared_font)


func _enter_editor_mode() -> void:
	editor_overlay.visible = true
	terminal_vbox.visible = false
	input_line.editable = false
	editor.grab_focus()


func _open_editor_mode(
	target_path: String,
	content: String,
	read_only: bool,
	display_mode: String,
	path_exists: bool) -> void:
	current_editor_path = target_path
	current_editor_read_only = read_only
	current_editor_display_mode = display_mode
	current_editor_path_exists = path_exists
	editor.text = content
	editor.editable = not read_only
	editor.set_caret_line(0)
	editor.set_caret_column(0)
	_enter_editor_mode()
	_show_editor_status_persistent(EDITOR_HELP_TEXT)


func _exit_editor_mode() -> void:
	_clear_editor_status()
	editor_overlay.visible = false
	terminal_vbox.visible = true
	input_line.editable = true
	editor.editable = true
	input_line.call_deferred("grab_focus")


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
		_save_editor_content()
		editor.accept_event()
		return


func _save_editor_content() -> void:
	if world_runtime == null:
		_show_editor_status_temporary("error: world runtime singleton '/root/WorldRuntime' not found.")
		return

	if current_editor_read_only and current_editor_display_mode == "hex":
		_show_editor_status_temporary("error: read-only buffer.")
		return

	if not world_runtime.has_method("SaveEditorContent"):
		_show_editor_status_temporary("error: runtime bridge method not found: SaveEditorContent.")
		return

	var response: Dictionary = world_runtime.call(
		"SaveEditorContent",
		current_node_id,
		current_user_id,
		current_cwd,
		current_editor_path,
		editor.text)

	if bool(response.get("ok", false)):
		var saved_path: String = str(response.get("savedPath", ""))
		if not saved_path.is_empty():
			current_editor_path = saved_path
		current_editor_path_exists = true
		var success_message := "saved."
		if not saved_path.is_empty():
			success_message = "saved: %s" % saved_path
		_show_editor_status_temporary(success_message)
		return

	var failure_message := _resolve_editor_save_message(response, "error: failed to save.")
	_show_editor_status_temporary(failure_message)


func _show_editor_status_persistent(message: String) -> void:
	editor_status_revision += 1
	editor_status_label.text = message


func _show_editor_status_temporary(message: String) -> void:
	editor_status_revision += 1
	var revision := editor_status_revision
	editor_status_label.text = message
	var timer := get_tree().create_timer(EDITOR_STATUS_TIMEOUT_SECONDS)
	timer.timeout.connect(func() -> void:
		if revision != editor_status_revision:
			return
		editor_status_label.text = "")


func _clear_editor_status() -> void:
	editor_status_revision += 1
	editor_status_label.text = ""


func _resolve_editor_save_message(response: Dictionary, fallback: String) -> String:
	var lines_variant: Variant = response.get("lines", [])
	var lines := _variant_lines_to_string_array(lines_variant)
	for line in lines:
		var message := str(line)
		if not message.is_empty():
			return message

	return fallback


func _variant_lines_to_string_array(lines_variant: Variant) -> Array[String]:
	var lines: Array[String] = []
	if lines_variant is Array:
		var lines_array: Array = lines_variant
		for line in lines_array:
			lines.append(str(line))

	return lines


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
	current_user_id = str(context.get("userId", ""))
	current_cwd = str(context.get("cwd", "/"))
	current_terminal_session_id = str(context.get("terminalSessionId", ""))
	prompt_user = str(context.get("promptUser", current_user_id))
	prompt_host = str(context.get("promptHost", current_node_id))
	var motd_lines_variant: Variant = context.get("motdLines", [])
	var motd_lines := _variant_lines_to_string_array(motd_lines_variant)
	if motd_lines.size() > 0:
		_append_output_batch(motd_lines, OUTPUT_SCROLL_MODE_MOTD_ANCHOR_ACTIVATE)


func _apply_systemcall_response(response: Dictionary) -> void:
	var previous_node_id := current_node_id
	var next_node_id: String = str(response.get("nextNodeId", ""))
	var lines := _variant_lines_to_string_array(response.get("lines", []))
	var is_connect_motd := lines.size() > 0 and not next_node_id.is_empty() and next_node_id != previous_node_id
	if is_connect_motd:
		_append_output_batch(lines, OUTPUT_SCROLL_MODE_MOTD_ANCHOR_ACTIVATE)
	else:
		_append_output_batch(lines)

	var next_cwd: String = str(response.get("nextCwd", ""))
	if not next_cwd.is_empty():
		current_cwd = next_cwd
	if not next_node_id.is_empty():
		current_node_id = next_node_id
	var next_user_id: String = str(response.get("nextUserId", ""))
	if not next_user_id.is_empty():
		current_user_id = next_user_id
	var next_prompt_user: String = str(response.get("nextPromptUser", ""))
	if not next_prompt_user.is_empty():
		prompt_user = next_prompt_user
	var next_prompt_host: String = str(response.get("nextPromptHost", ""))
	if not next_prompt_host.is_empty():
		prompt_host = next_prompt_host

	_refresh_prompt()

	var should_open_editor: bool = bool(response.get("openEditor", false))
	if should_open_editor:
		var editor_path: String = str(response.get("editorPath", ""))
		var editor_content: String = str(response.get("editorContent", ""))
		var editor_read_only: bool = bool(response.get("editorReadOnly", false))
		var editor_display_mode: String = str(response.get("editorDisplayMode", "text"))
		var editor_path_exists: bool = bool(response.get("editorPathExists", true))
		_open_editor_mode(
			editor_path,
			editor_content,
			editor_read_only,
			editor_display_mode,
			editor_path_exists)


func _request_program_interrupt() -> void:
	if world_runtime == null:
		return
	if not world_runtime.has_method("InterruptTerminalProgramExecution"):
		return

	var response_variant: Variant = world_runtime.call(
		"InterruptTerminalProgramExecution",
		current_terminal_session_id)
	if response_variant is Dictionary:
		var response: Dictionary = response_variant
		_apply_systemcall_response(response)

	if world_runtime.has_method("IsTerminalProgramRunning"):
		var running_variant: Variant = world_runtime.call("IsTerminalProgramRunning", current_terminal_session_id)
		is_program_running = bool(running_variant)
	else:
		is_program_running = false


func _refresh_prompt() -> void:
	var display_cwd := current_cwd
	if display_cwd.is_empty():
		display_cwd = "/"

	if display_cwd.begins_with("/home/" + prompt_user):
		var suffix := display_cwd.substr(("/home/" + prompt_user).length())
		display_cwd = "~" + suffix

	prompt_label.text = "%s@%s:%s $ " % [prompt_user, prompt_host, display_cwd]
