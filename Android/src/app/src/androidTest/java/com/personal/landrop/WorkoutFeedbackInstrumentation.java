package com.personal.landrop;

import android.app.Activity;
import android.app.Instrumentation;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Bundle;
import android.view.View;
import android.view.ViewGroup;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.List;

/** Isolated JSON/merge and view regression. No temporary task enters saved user tasks. */
public final class WorkoutFeedbackInstrumentation extends Instrumentation {
    private final StringBuilder results = new StringBuilder();
    private Activity activity;
    private Class<?> taskClass;
    private Class<?> exerciseClass;
    private Class<?> setClass;
    private Bundle arguments;

    @Override public void onCreate(Bundle arguments) { super.onCreate(arguments); this.arguments = arguments; start(); }

    @Override public void onStart() {
        Bundle result = new Bundle();
        int code = Activity.RESULT_OK;
        try {
            Intent intent = new Intent(getTargetContext(), MainActivity.class);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
            activity = startActivitySync(intent);
            taskClass = Class.forName("com.personal.landrop.MainActivity$WorkoutTask");
            exerciseClass = Class.forName("com.personal.landrop.MainActivity$WorkoutExercise");
            setClass = Class.forName("com.personal.landrop.MainActivity$WorkoutSet");
            runOnMainSync(() -> {
                try {
                    if (getTargetContext().getPackageName().equals("com.personal.landrop.fitnesscheck")) ((List<?>)get(activity, "tasks")).clear();
                    verify();
                } catch (Exception error) { throw new RuntimeException(error); }
            });
            verifyFeedbackUi();
            if (arguments != null && "true".equals(arguments.getString("sync"))) verifySync();
        } catch (Throwable error) {
            code = Activity.RESULT_CANCELED;
            results.append("FAIL: ").append(error.toString()).append('\n');
        } finally {
            if (activity != null) runOnMainSync(() -> {
                try {
                    set(activity, "activeWorkoutTask", null);
                    // The existing return helper restores the task workspace without saving test data.
                    call("returnToTaskWorkspace", new Class<?>[0]);
                } catch (Exception ignored) { }
            });
            result.putString("stream", results.toString());
            finish(code, result);
        }
    }

    @SuppressWarnings("unchecked")
    private void verify() throws Exception {
        String oldJson = "{\"id\":\"fitness-legacy-only\",\"title\":\"旧任务测试\",\"status\":\"completed\",\"updatedAt\":10,\"exercises\":[{\"id\":\"old\",\"name\":\"旧动作\",\"sets\":[{\"index\":1,\"completed\":true,\"completedAt\":100},{\"index\":2,\"completed\":true,\"completedAt\":200},{\"index\":3,\"completed\":false}]}]}";
        Object old = call("taskFromJson", new Class<?>[]{JSONObject.class}, new JSONObject(oldJson));
        Object exercise = ((List<?>)get(old, "exercises")).get(0);
        Object last = call("lastCompletedSet", new Class<?>[]{exerciseClass}, exercise);
        require((int)get(last, "index") == 2, "Default selects last completed group");
        JSONObject legacy = (JSONObject)call("taskToJson", new Class<?>[]{taskClass}, old);
        JSONObject legacySet = legacy.getJSONArray("exercises").getJSONObject(0).getJSONArray("sets").getJSONObject(1);
        require(legacy.isNull("effort") && legacySet.isNull("rir") && legacySet.isNull("movementQuality") && legacySet.isNull("discomfort"), "Missing feedback remains JSON null");
        View legacyView = (View)call("buildWorkoutSummary", new Class<?>[]{taskClass}, old);
        require(findText(legacyView, "未记录"), "Old settlement renders unrecorded feedback");

        File directory = activity.getExternalFilesDir("fitness-test");
        File source = new File(directory, "windows-out.json");
        JSONArray windows = new JSONArray(new String(Files.readAllBytes(source.toPath()), StandardCharsets.UTF_8));
        Object task = call("taskFromJson", new Class<?>[]{JSONObject.class}, windows.getJSONObject(0));
        JSONObject roundtrip = (JSONObject)call("taskToJson", new Class<?>[]{taskClass}, task);
        JSONObject first = roundtrip.getJSONArray("exercises").getJSONObject(0);
        require(first.getJSONArray("sets").getJSONObject(0).getString("rir").equals("0")
                && first.getJSONArray("sets").getJSONObject(1).getString("rir").equals("4+"), "Windows zero and 4+ RIR survive Android roundtrip");
        require(first.getJSONArray("sets").getJSONObject(2).isNull("rir"), "Unfinished group not assigned feedback");
        require(roundtrip.getJSONArray("exercises").getJSONObject(2).getBoolean("isAdditional")
                && roundtrip.getJSONArray("exercises").getJSONObject(1).getString("skipReason").equals("不舒服没做"), "Additional action and skipped reason survive Android roundtrip");

        List<Object> realTasks = (List<Object>)get(activity, "tasks");
        List<Object> originals = new ArrayList<>(realTasks);
        try {
            realTasks.clear();
            require((int)call("mergeTasks", new Class<?>[]{JSONArray.class}, windows) == 1, "Windows snapshot merges into Android");
            require((int)call("mergeTasks", new Class<?>[]{JSONArray.class}, new JSONArray().put(new JSONObject(oldJson).put("id", "fitness-regression-only"))) == 0, "Older snapshot cannot erase feedback");
        } finally { realTasks.clear(); realTasks.addAll(originals); }

        Object plan = ((List<?>)get(task, "exercises")).get(0);
        List<?> sets = (List<?>)get(plan, "sets");
        Object evaluated = sets.get(1);
        for (String value : new String[]{"0", "1", "2", "3", "4+", "uncertain"}) {
            set(evaluated, "rir", value);
            JSONObject encoded = (JSONObject)call("taskToJson", new Class<?>[]{taskClass}, task);
            Object decoded = call("taskFromJson", new Class<?>[]{JSONObject.class}, encoded);
            Object decodedSet = ((List<?>)get(((List<?>)get(decoded, "exercises")).get(0), "sets")).get(1);
            require(value.equals(get(decodedSet, "rir")), "RIR option survives: " + value);
        }
        set(evaluated, "rir", "4+");
        set(evaluated, "movementQuality", "compensated");
        set(evaluated, "discomfort", "present");
        set(evaluated, "discomfortNotes", "测试：右肩拉扯");
        set(task, "feedbackNotes", "Android 补填回归");
        long updated = (long)get(task, "updatedAt");
        long completedAt = (long)get(task, "completedAt");
        SharedPreferences preferences = activity.getPreferences(Activity.MODE_PRIVATE);
        String originalRaw = preferences.getString("tasks", null);
        try {
            call("touchTask", new Class<?>[]{taskClass}, task);
            require((long)get(task, "updatedAt") > updated && (long)get(task, "completedAt") == completedAt, "Supplement updates timestamp and preserves completion");
        } finally {
            SharedPreferences.Editor edit = preferences.edit();
            if (originalRaw == null) edit.remove("tasks"); else edit.putString("tasks", originalRaw);
            edit.commit();
        }
        JSONObject output = (JSONObject)call("taskToJson", new Class<?>[]{taskClass}, task);
        require(output.getJSONArray("exercises").getJSONObject(0).getJSONArray("sets").getJSONObject(1).getInt("actualReps") == 9, "Actual group history preserved");
        try (FileOutputStream file = new FileOutputStream(new File(directory, "android-roundtrip.json"))) {
            file.write(new JSONArray().put(output).toString().getBytes(StandardCharsets.UTF_8));
        }
        // Deliberately never insert task into realTasks. Screenshots show only the synthetic task.
        set(activity, "activeWorkoutTask", task);
        activity.setContentView((View)call("buildWorkoutSummary", new Class<?>[]{taskClass}, task));
        require(findText(activity.findViewById(android.R.id.content), "临时新增"), "Settlement exposes supplemented exercise and feedback");
    }

    @SuppressWarnings("unchecked")
    private void verifySync() throws Exception {
        require(getTargetContext().getPackageName().equals("com.personal.landrop.fitnesscheck"), "Live sync runs only in independent test package");
        File directory = activity.getExternalFilesDir("fitness-test");
        JSONObject pair = new JSONObject(new String(Files.readAllBytes(new File(directory, "windows-pair.json").toPath()), StandardCharsets.UTF_8));
        JSONArray snapshot = new JSONArray(new String(Files.readAllBytes(new File(directory, "android-roundtrip.json").toPath()), StandardCharsets.UTF_8));
        JSONObject task = snapshot.getJSONObject(0);
        task.put("feedbackNotes", "Android live sync");
        task.put("updatedAt", Math.max(System.currentTimeMillis(), task.getLong("updatedAt") + 1));
        runOnMainSync(() -> {
            try {
                require(((List<?>)get(activity, "notes")).isEmpty(), "Independent sync contains no user notes");
                List<Object> tasks = (List<Object>)get(activity, "tasks");
                tasks.clear();
                tasks.add(call("taskFromJson", new Class<?>[]{JSONObject.class}, task));
                call("saveTasks", new Class<?>[0]);
                set(activity, "activeWorkoutTask", null);
                String host = "lan".equals(arguments.getString("transport")) ? pair.getString("host") : "127.0.0.1";
                String qr = "landrop://pair?host=" + host + "&port=" + pair.getInt("port") + "&token=" + pair.getString("token") + "&device=fitness-regression";
                call("connectFromQr", new Class<?>[]{String.class}, qr);
            } catch (Exception error) { throw new RuntimeException(error); }
        });
        await(() -> Boolean.TRUE.equals(get(activity, "connected")), "Independent Android and Windows authenticate");
        Object beforeFirst = ((List<?>)get(activity, "tasks")).get(0);
        runOnMainSync(() -> {
            try { call("syncAll", new Class<?>[0]); } catch (Exception error) { throw new RuntimeException(error); }
        });
        await(() -> ((List<?>)get(activity, "tasks")).get(0) != beforeFirst, "First manual sync returns merged snapshot");
        Thread.sleep(600);
        runOnMainSync(() -> {
            try { call("syncAll", new Class<?>[0]); } catch (Exception error) { throw new RuntimeException(error); }
        });
        await(() -> ((List<?>)get(activity, "tasks")).size() == 1 && "Windows 回传补填".equals(get(((List<?>)get(activity, "tasks")).get(0), "effort")), "Android feedback and Windows supplementation complete roundtrip");
        Object before = ((List<?>)get(activity, "tasks")).get(0);
        runOnMainSync(() -> {
            try { call("syncAll", new Class<?>[0]); } catch (Exception error) { throw new RuntimeException(error); }
        });
        await(() -> ((List<?>)get(activity, "tasks")).get(0) != before, "Second manual sync receives merged snapshot");
        runOnMainSync(() -> {
            try {
                Object synced = ((List<?>)get(activity, "tasks")).get(0);
                JSONObject output = (JSONObject)call("taskToJson", new Class<?>[]{taskClass}, synced);
                JSONObject original = output.getJSONArray("exercises").getJSONObject(0);
                require(original.getJSONArray("sets").getJSONObject(1).getString("movementQuality").equals("compensated")
                        && original.getJSONArray("sets").getJSONObject(1).getString("discomfortNotes").equals("测试：右肩拉扯"), "Live roundtrip preserves Android group feedback");
                require(!original.getJSONArray("sets").getJSONObject(2).getBoolean("completed")
                        && original.getJSONArray("sets").getJSONObject(2).isNull("rir"), "Live sync preserves incomplete group and null feedback");
                require(output.getJSONArray("exercises").getJSONObject(2).getBoolean("isAdditional")
                        && output.getJSONArray("exercises").getJSONObject(1).getString("skipReason").equals("不舒服没做"), "Live sync preserves added exercise and skip reason");
                try (FileOutputStream file = new FileOutputStream(new File(directory, "android-live-sync.json"))) {
                    file.write(new JSONArray().put(output).toString().getBytes(StandardCharsets.UTF_8));
                }
            } catch (Exception error) { throw new RuntimeException(error); }
        });
    }

    private void verifyFeedbackUi() throws Exception {
        require(getTargetContext().getPackageName().equals("com.personal.landrop.fitnesscheck"), "UI regression isolated from user data");
        File directory = activity.getExternalFilesDir("fitness-test");
        JSONArray saved = new JSONArray(new String(Files.readAllBytes(new File(directory, "android-roundtrip.json").toPath()), StandardCharsets.UTF_8));
        Object task = call("taskFromJson", new Class<?>[]{JSONObject.class}, saved.getJSONObject(0));
        Object exercise = ((List<?>)get(task, "exercises")).get(0);
        Object set = ((List<?>)get(exercise, "sets")).get(1);
        long timestamp = (long)get(task, "updatedAt");
        runOnMainSync(() -> {
            try {
                call("openWorkoutTask", new Class<?>[]{taskClass}, task);
                call("showSetFeedback", new Class<?>[]{taskClass, exerciseClass, setClass, Runnable.class}, task, exercise, set, (Runnable)() -> { });
            } catch (Exception error) { throw new RuntimeException(error); }
        });
        waitForIdleSync();
        Thread.sleep(500);
        android.graphics.Bitmap screenshot = getUiAutomation().takeScreenshot();
        if (screenshot != null) try (FileOutputStream file = new FileOutputStream(new File(directory, "feedback-dialog.png"))) { screenshot.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, file); }
        clickText("还能规范做几次：4 次以上");
        clickText("1 次");
        clickText("动作保持得怎么样：后几次幅度缩小或借力");
        clickText("动作不熟、不确定");
        clickText("有没有不舒服：有");
        clickText("无");
        clickText("保存反馈");
        require("1".equals(get(set, "rir")) && "uncertain".equals(get(set, "movementQuality")) && "none".equals(get(set, "discomfort")) && get(set, "discomfortNotes") == null, "Real picker and save actions persist selected group feedback");
        require((long)get(task, "updatedAt") > timestamp && (long)get(task, "completedAt") == 300, "UI save updates timestamp without changing settlement");
        Object other = ((List<?>)get(exercise, "sets")).get(0);
        require("0".equals(get(other, "rir")), "Editing group 2 does not alter group 1");
        long savedAt = (long)get(task, "updatedAt");
        runOnMainSync(() -> {
            try { call("showSetFeedback", new Class<?>[]{taskClass, exerciseClass, setClass, Runnable.class}, task, exercise, set, (Runnable)() -> { }); }
            catch (Exception error) { throw new RuntimeException(error); }
        });
        clickText("暂不填写");
        require((long)get(task, "updatedAt") == savedAt, "Skipping feedback does not change data");
        runOnMainSync(() -> {
            try { call("showTaskFeedback", new Class<?>[]{taskClass, Runnable.class}, task, (Runnable)() -> { }); }
            catch (Exception error) { throw new RuntimeException(error); }
        });
        clickText("有没有不舒服：有");
        clickText("未记录");
        setEditor(0, "训练很费力");
        setEditor(2, "结算后补填的备注");
        clickText("保存反馈");
        require("训练很费力".equals(get(task, "effort")) && "结算后补填的备注".equals(get(task, "feedbackNotes")) && get(task, "discomfort") == null, "Completed task accepts partial overall feedback without implicit no discomfort");
        runOnMainSync(() -> {
            try {
                activity.setContentView((View)call("buildWorkoutTaskView", new Class<?>[]{taskClass}, task));
                android.widget.Button button = (android.widget.Button)call("addExerciseButton", new Class<?>[]{taskClass}, task);
                button.performClick();
            } catch (Exception error) { throw new RuntimeException(error); }
        });
        setEditor(0, "大剪刀 UI 测试");
        clickText("添加动作");
        List<?> exercises = (List<?>)get(task, "exercises");
        Object added = exercises.get(exercises.size() - 1);
        require(Boolean.TRUE.equals(get(added, "isAdditional")) && ((List<?>)get(added, "sets")).size() == 4 && !Boolean.TRUE.equals(get(((List<?>)get(added, "sets")).get(0), "completed")), "UI adds four uncompleted groups without changing original plan");
        runOnMainSync(() -> {
            try { call("showSkipReason", new Class<?>[]{taskClass, exerciseClass, Runnable.class}, task, exercise, (Runnable)() -> { }); }
            catch (Exception error) { throw new RuntimeException(error); }
        });
        setEditor(0, "右肩不适，没做");
        clickText("保存原因");
        require("右肩不适，没做".equals(get(exercise, "skipReason")) && !Boolean.TRUE.equals(get(((List<?>)get(exercise, "sets")).get(2), "completed")), "UI skip reason does not mark unfinished group complete");
        Object finalSet = ((List<?>)get(exercise, "sets")).get(2);
        final android.widget.Button[] checkbox = new android.widget.Button[1];
        runOnMainSync(() -> {
            try {
                set(task, "status", "active");
                call("openWorkoutTask", new Class<?>[]{taskClass}, task);
                android.widget.LinearLayout row = (android.widget.LinearLayout)call("workoutSetRow",
                        new Class<?>[]{taskClass, exerciseClass, setClass, Runnable.class}, task, exercise, finalSet, (Runnable)() -> { });
                checkbox[0] = (android.widget.Button)row.getChildAt(row.getChildCount() - 1);
                checkbox[0].performClick();
            } catch (Exception error) { throw new RuntimeException(error); }
        });
        clickText("暂不填写");
        require(Boolean.TRUE.equals(get(finalSet, "completed")) && (long)get(finalSet, "completedAt") > 0
                && get(finalSet, "rir") == null && "uncertain".equals(get(set, "movementQuality")), "Final group completion prompts optional feedback without remapping prior evaluation");
        require(((Number)get(activity, "restSecondsRemaining")).longValue() > 0, "Group completion retains rest timer");
        runOnMainSync(() -> checkbox[0].performClick());
        require(!Boolean.TRUE.equals(get(finalSet, "completed")) && (long)get(finalSet, "completedAt") == 0
                && ((Number)get(activity, "restSecondsRemaining")).longValue() == 0, "Unchecking preserves original cancellation and rest behavior");
        waitForIdleSync();
        android.os.SystemClock.sleep(500);
        screenshot = getUiAutomation().takeScreenshot();
        if (screenshot != null) try (FileOutputStream file = new FileOutputStream(new File(directory, "silver-workout.png"))) {
            screenshot.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, file);
        }
    }

    private void clickText(String value) throws Exception {
        long deadline = android.os.SystemClock.uptimeMillis() + 5000;
        while (android.os.SystemClock.uptimeMillis() < deadline) {
            waitForIdleSync();
            android.view.accessibility.AccessibilityNodeInfo root = getUiAutomation().getRootInActiveWindow();
            if (root != null) for (android.view.accessibility.AccessibilityNodeInfo item : root.findAccessibilityNodeInfosByText(value)) {
                if (!value.equals(String.valueOf(item.getText()))) continue;
                android.view.accessibility.AccessibilityNodeInfo target = item;
                while (target != null && !target.isClickable()) target = target.getParent();
                if (target != null && target.performAction(android.view.accessibility.AccessibilityNodeInfo.ACTION_CLICK)) { waitForIdleSync(); Thread.sleep(150); return; }
            }
            Thread.sleep(100);
        }
        throw new AssertionError("Could not click: " + value);
    }

    private void setEditor(int index, String value) throws Exception {
        waitForIdleSync();
        List<android.view.accessibility.AccessibilityNodeInfo> editors = new ArrayList<>();
        collectEditors(getUiAutomation().getRootInActiveWindow(), editors);
        if (index >= editors.size()) throw new AssertionError("Missing editor " + index);
        Bundle arguments = new Bundle();
        arguments.putCharSequence(android.view.accessibility.AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, value);
        require(editors.get(index).performAction(android.view.accessibility.AccessibilityNodeInfo.ACTION_SET_TEXT, arguments), "UI input field " + index);
        waitForIdleSync();
    }

    private void collectEditors(android.view.accessibility.AccessibilityNodeInfo node, List<android.view.accessibility.AccessibilityNodeInfo> output) {
        if (node == null) return;
        if ("android.widget.EditText".contentEquals(node.getClassName())) output.add(node);
        for (int i = 0; i < node.getChildCount(); i++) collectEditors(node.getChild(i), output);
    }

    private interface Check { boolean value() throws Exception; }
    private void await(Check check, String label) throws Exception {
        long deadline = android.os.SystemClock.uptimeMillis() + 20000;
        while (android.os.SystemClock.uptimeMillis() < deadline) {
            if (check.value()) { require(true, label); return; }
            Thread.sleep(100);
        }
        throw new AssertionError("Timed out: " + label);
    }

    private void require(boolean condition, String label) { if (!condition) throw new AssertionError(label); results.append("PASS: ").append(label).append('\n'); }
    private Object call(String name, Class<?>[] types, Object... args) throws Exception {
        Method method = MainActivity.class.getDeclaredMethod(name, types); method.setAccessible(true); return method.invoke(activity, args);
    }
    private static Object get(Object target, String name) throws Exception { Field field = target.getClass().getDeclaredField(name); field.setAccessible(true); return field.get(target); }
    private static void set(Object target, String name, Object value) throws Exception { Field field = target.getClass().getDeclaredField(name); field.setAccessible(true); field.set(target, value); }
    private static boolean findText(View view, String value) {
        if (view instanceof TextView && ((TextView)view).getText().toString().contains(value)) return true;
        if (view instanceof ViewGroup) for (int i = 0; i < ((ViewGroup)view).getChildCount(); i++) if (findText(((ViewGroup)view).getChildAt(i), value)) return true;
        return false;
    }
}
