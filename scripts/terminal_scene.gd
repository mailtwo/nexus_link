extends Control

@export var max_scrollback_lines: int = 1000
@export var terminal_background_color: Color = Color(0.0470588, 0.0588235, 0.0705882, 1.0)
@export var terminal_text_color: Color = Color(0.839216, 0.976471, 0.858824, 1.0)
@export var terminal_caret_color: Color = Color(0.545098, 0.952941, 0.639216, 1.0)
@export var terminal_selection_color: Color = Color(0.22, 0.35, 0.24, 1.0)
@export var terminal_font_size: int = 18
@export var event_poll_interval_seconds: float = 0.10
@export var max_command_history_entries: int = 200

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
var command_history: Array[String] = []
var history_nav_index: int = -1
var history_nav_draft: String = ""
var is_applying_input_text: bool = false
var completion_session_active: bool = false
var completion_anchor_text: String = ""
var completion_anchor_token_raw_start: int = -1
var completion_anchor_token_raw_end: int = -1
var completion_candidate_texts: Array[String] = []
var completion_candidate_index: int = -1
var completion_last_applied_text: String = ""
var completion_last_applied_caret: int = -1

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
	input_line.text_changed.connect(_on_input_line_text_changed)
	input_line.focus_exited.connect(_on_input_line_focus_exited)
	input_line.gui_input.connect(_on_input_line_gui_input)
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
	_sync_completion_session_with_input_state()

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

	_reset_completion_session()
	var trimmed: String = command_text.strip_edges()
	if trimmed.is_empty():
		_reset_history_navigation()
		input_line.clear()
		input_line.call_deferred("grab_focus")
		return

	_record_command_history(trimmed)
	_reset_history_navigation()
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


func _on_input_line_gui_input(event: InputEvent) -> void:
	if is_applying_input_text:
		return
	if event is not InputEventKey:
		return

	var key_event := event as InputEventKey
	if not key_event.pressed:
		return
	if editor_overlay.visible or is_program_running:
		return
	if not input_line.has_focus():
		return
	if _try_handle_tab_completion(key_event):
		input_line.accept_event()
		return
	if _try_handle_history_navigation(key_event):
		input_line.accept_event()


func _on_input_line_text_changed(_new_text: String) -> void:
	if is_applying_input_text:
		return
	_reset_completion_session()


func _on_input_line_focus_exited() -> void:
	_reset_completion_session()


func _try_handle_tab_completion(event: InputEventKey) -> bool:
	if event.keycode != KEY_TAB:
		return false
	if event.shift_pressed:
		return false

	_sync_completion_session_with_input_state()
	if completion_session_active:
		_cycle_completion_candidate()
		return true

	return _start_completion_session()


func _try_handle_history_navigation(event: InputEventKey) -> bool:
	if event.keycode == KEY_UP:
		_navigate_history(-1)
		return true

	if event.keycode == KEY_DOWN:
		_navigate_history(1)
		return true

	return false


func _start_completion_session() -> bool:
	var current_text := input_line.text
	var current_caret := clampi(input_line.caret_column, 0, current_text.length())
	var target := _resolve_completion_target(current_text, current_caret)

	var candidates: Array[String] = []
	if _is_path_completion_mode(target):
		candidates = _build_path_completion_candidates(target)
	else:
		candidates = _build_command_completion_candidates(target)

	if candidates.is_empty():
		_reset_completion_session()
		return true

	completion_session_active = true
	completion_anchor_text = current_text
	completion_anchor_token_raw_start = int(target.get("raw_start", current_caret))
	completion_anchor_token_raw_end = int(target.get("raw_end", current_caret))
	completion_candidate_texts = candidates
	completion_candidate_index = 0
	_apply_completion_candidate(completion_candidate_index)
	return true


func _cycle_completion_candidate() -> void:
	if not completion_session_active:
		return
	if completion_candidate_texts.is_empty():
		_reset_completion_session()
		return

	completion_candidate_index += 1
	if completion_candidate_index >= completion_candidate_texts.size():
		completion_candidate_index = 0
	_apply_completion_candidate(completion_candidate_index)


func _apply_completion_candidate(candidate_index: int) -> void:
	if not completion_session_active:
		return
	if candidate_index < 0 or candidate_index >= completion_candidate_texts.size():
		return

	var anchor_start := maxi(0, completion_anchor_token_raw_start)
	var anchor_end := maxi(anchor_start, completion_anchor_token_raw_end)
	var prefix := completion_anchor_text.substr(0, anchor_start)
	var suffix := completion_anchor_text.substr(anchor_end)
	var replacement := completion_candidate_texts[candidate_index]
	var next_text := prefix + replacement + suffix
	var next_caret := prefix.length() + replacement.length()
	_set_input_text_and_caret(next_text, next_caret)
	completion_last_applied_text = next_text
	completion_last_applied_caret = next_caret


func _sync_completion_session_with_input_state() -> void:
	if not completion_session_active:
		return
	if is_applying_input_text:
		return
	if not input_line.has_focus():
		_reset_completion_session()
		return
	if input_line.text != completion_last_applied_text:
		_reset_completion_session()
		return
	if input_line.caret_column != completion_last_applied_caret:
		_reset_completion_session()


func _reset_completion_session() -> void:
	completion_session_active = false
	completion_anchor_text = ""
	completion_anchor_token_raw_start = -1
	completion_anchor_token_raw_end = -1
	completion_candidate_texts = []
	completion_candidate_index = -1
	completion_last_applied_text = ""
	completion_last_applied_caret = -1


func _is_path_completion_mode(target: Dictionary) -> bool:
	var token_index := int(target.get("token_index", 0))
	if token_index > 0:
		return true

	var prefix := str(target.get("prefix", ""))
	return prefix.contains("/")


func _build_command_completion_candidates(target: Dictionary) -> Array[String]:
	var candidates: Array[String] = []
	if world_runtime == null:
		return candidates
	if not world_runtime.has_method("GetTerminalCommandCompletions"):
		return candidates

	var prefix := str(target.get("prefix", ""))
	var is_quoted := bool(target.get("is_quoted", false))
	var has_closing_quote := bool(target.get("has_closing_quote", false))
	var completions_variant: Variant = world_runtime.call(
		"GetTerminalCommandCompletions",
		current_node_id,
		current_user_id,
		current_cwd)
	var completions := _variant_lines_to_string_array(completions_variant)
	for completion in completions:
		if not prefix.is_empty() and not completion.begins_with(prefix):
			continue

		if is_quoted:
			candidates.append(_build_quoted_completion_replacement(completion, false, has_closing_quote))
		else:
			candidates.append(completion + " ")

	return candidates


func _build_path_completion_candidates(target: Dictionary) -> Array[String]:
	var candidates: Array[String] = []
	if world_runtime == null:
		return candidates
	if not world_runtime.has_method("GetTerminalPathCompletionEntries"):
		return candidates

	var prefix := str(target.get("prefix", ""))
	var split := _split_completion_path_prefix(prefix)
	var dir_part := str(split.get("dir_part", ""))
	var name_prefix := str(split.get("name_prefix", ""))
	var directory_query := "." if dir_part.is_empty() else dir_part
	var payload := _fetch_path_completion_payload(directory_query)
	if not bool(payload.get("ok", false)):
		return candidates

	var entries_variant: Variant = payload.get("entries", [])
	if entries_variant is not Array:
		return candidates

	var entries: Array = entries_variant
	var is_quoted := bool(target.get("is_quoted", false))
	var has_closing_quote := bool(target.get("has_closing_quote", false))
	for entry_variant in entries:
		if entry_variant is not Dictionary:
			continue

		var entry: Dictionary = entry_variant
		var entry_name := str(entry.get("name", ""))
		if entry_name.is_empty():
			continue
		if not name_prefix.is_empty() and not entry_name.begins_with(name_prefix):
			continue
		if not name_prefix.begins_with(".") and entry_name.begins_with("."):
			continue

		var content := dir_part + entry_name
		var is_directory := bool(entry.get("isDirectory", false))
		if is_quoted:
			candidates.append(_build_quoted_completion_replacement(content, is_directory, has_closing_quote))
		elif is_directory:
			candidates.append(content + "/")
		else:
			candidates.append(content + " ")

	return candidates


func _fetch_path_completion_payload(directory_query: String) -> Dictionary:
	var payload := {
		"ok": false,
		"entries": [],
	}
	if world_runtime == null:
		return payload
	if not world_runtime.has_method("GetTerminalPathCompletionEntries"):
		return payload

	var response_variant: Variant = world_runtime.call(
		"GetTerminalPathCompletionEntries",
		current_node_id,
		current_user_id,
		current_cwd,
		directory_query)
	if response_variant is not Dictionary:
		return payload

	var response: Dictionary = response_variant
	payload["ok"] = bool(response.get("ok", false))
	payload["entries"] = response.get("entries", [])
	return payload


func _split_completion_path_prefix(prefix: String) -> Dictionary:
	var split_index := prefix.rfind("/")
	if split_index < 0:
		return {
			"dir_part": "",
			"name_prefix": prefix,
		}

	return {
		"dir_part": prefix.substr(0, split_index + 1),
		"name_prefix": prefix.substr(split_index + 1),
	}


func _resolve_completion_target(text: String, caret_column: int) -> Dictionary:
	var caret := clampi(caret_column, 0, text.length())
	var tokens := _collect_completion_tokens(text)
	if tokens.is_empty():
		return {
			"token_index": 0,
			"raw_start": caret,
			"raw_end": caret,
			"is_quoted": false,
			"has_closing_quote": false,
			"content": "",
			"prefix": "",
		}

	for token_index in range(tokens.size()):
		var token: Dictionary = tokens[token_index]
		var raw_start := int(token.get("raw_start", 0))
		var raw_end := int(token.get("raw_end", raw_start))
		if caret < raw_start:
			return {
				"token_index": token_index,
				"raw_start": caret,
				"raw_end": caret,
				"is_quoted": false,
				"has_closing_quote": false,
				"content": "",
				"prefix": "",
			}

		if caret > raw_end:
			continue

		var raw_token := text.substr(raw_start, raw_end - raw_start)
		var caret_in_token := clampi(caret - raw_start, 0, raw_token.length())
		var decoded := _decode_completion_token(raw_token, caret_in_token)
		return {
			"token_index": token_index,
			"raw_start": raw_start,
			"raw_end": raw_end,
			"is_quoted": bool(token.get("is_quoted", false)),
			"has_closing_quote": bool(token.get("has_closing_quote", false)),
			"content": str(decoded.get("content", "")),
			"prefix": str(decoded.get("prefix", "")),
		}

	return {
		"token_index": tokens.size(),
		"raw_start": caret,
		"raw_end": caret,
		"is_quoted": false,
		"has_closing_quote": false,
		"content": "",
		"prefix": "",
	}


func _collect_completion_tokens(text: String) -> Array[Dictionary]:
	var tokens: Array[Dictionary] = []
	var token_started := false
	var token_raw_start := 0
	var token_is_quoted := false
	var token_has_closing_quote := false
	var in_quotes := false
	var escape_next := false
	for index in range(text.length()):
		var ch := text.substr(index, 1)
		if not token_started:
			if _is_completion_whitespace(ch):
				continue

			token_started = true
			token_raw_start = index
			token_is_quoted = ch == "\""
			token_has_closing_quote = false
			in_quotes = token_is_quoted
			escape_next = false
			continue

		if in_quotes:
			if escape_next:
				escape_next = false
				continue

			if ch == "\\":
				escape_next = true
				continue

			if ch == "\"":
				in_quotes = false
				if token_is_quoted:
					token_has_closing_quote = true
				continue

			continue

		if ch == "\"":
			in_quotes = true
			continue

		if _is_completion_whitespace(ch):
			tokens.append({
				"raw_start": token_raw_start,
				"raw_end": index,
				"is_quoted": token_is_quoted,
				"has_closing_quote": token_is_quoted and token_has_closing_quote and not in_quotes,
			})
			token_started = false
			in_quotes = false
			escape_next = false

	if token_started:
		tokens.append({
			"raw_start": token_raw_start,
			"raw_end": text.length(),
			"is_quoted": token_is_quoted,
			"has_closing_quote": token_is_quoted and token_has_closing_quote and not in_quotes,
		})

	return tokens


func _decode_completion_token(raw_token: String, caret_in_token: int) -> Dictionary:
	var content := ""
	var prefix := ""
	var in_quotes := false
	var escape_next := false
	var normalized_caret := clampi(caret_in_token, 0, raw_token.length())
	for index in range(raw_token.length()):
		var ch := raw_token.substr(index, 1)
		var append_char := true
		if escape_next:
			escape_next = false
		elif ch == "\\" and in_quotes:
			escape_next = true
			append_char = false
		elif ch == "\"":
			in_quotes = not in_quotes
			append_char = false

		if append_char:
			content += ch
			if index < normalized_caret:
				prefix += ch

	if escape_next:
		content += "\\"
		if raw_token.length() <= normalized_caret:
			prefix += "\\"

	return {
		"content": content,
		"prefix": prefix,
	}


func _is_completion_whitespace(ch: String) -> bool:
	return ch == " " or ch == "\t" or ch == "\n" or ch == "\r"


func _build_quoted_completion_replacement(content: String, is_directory: bool, has_closing_quote: bool) -> String:
	var escaped_content := _escape_for_double_quote(content)
	if is_directory:
		var replacement := "\"" + escaped_content + "/"
		if has_closing_quote:
			replacement += "\""
		return replacement

	return "\"" + escaped_content + "\" "


func _escape_for_double_quote(value: String) -> String:
	var escaped := ""
	for index in range(value.length()):
		var ch := value.substr(index, 1)
		if ch == "\\" or ch == "\"":
			escaped += "\\"
		escaped += ch

	return escaped


func _navigate_history(direction: int) -> void:
	_reset_completion_session()
	if command_history.is_empty():
		return
	if direction < 0:
		if history_nav_index == -1:
			history_nav_draft = input_line.text
			history_nav_index = command_history.size() - 1
		else:
			history_nav_index = maxi(0, history_nav_index - 1)
		_set_input_text_and_caret_to_end(command_history[history_nav_index])
		return

	if direction > 0:
		if history_nav_index == -1:
			return

		var last_index := command_history.size() - 1
		if history_nav_index >= last_index:
			history_nav_index = -1
			_set_input_text_and_caret_to_end(history_nav_draft)
			return

		history_nav_index += 1
		_set_input_text_and_caret_to_end(command_history[history_nav_index])


func _record_command_history(command_text: String) -> void:
	var trimmed := command_text.strip_edges()
	if trimmed.is_empty():
		return

	var limit := maxi(0, max_command_history_entries)
	if limit == 0:
		command_history.clear()
		return

	command_history.append(trimmed)
	if command_history.size() <= limit:
		return

	command_history = command_history.slice(command_history.size() - limit, command_history.size())


func _reset_history_navigation() -> void:
	history_nav_index = -1
	history_nav_draft = ""


func _set_input_text_and_caret(value: String, caret_column: int) -> void:
	var clamped_caret := clampi(caret_column, 0, value.length())
	is_applying_input_text = true
	input_line.text = value
	input_line.caret_column = clamped_caret
	is_applying_input_text = false


func _set_input_text_and_caret_to_end(value: String) -> void:
	_set_input_text_and_caret(value, value.length())


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
