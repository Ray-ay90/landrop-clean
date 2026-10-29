package com.personal.landrop;

import android.app.Activity;
import android.app.Dialog;
import android.Manifest;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.drawable.ColorDrawable;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.CountDownTimer;
import android.os.Environment;
import android.os.Looper;
import android.provider.MediaStore;
import android.provider.OpenableColumns;
import android.text.Editable;
import android.text.InputType;
import android.text.Layout;
import android.text.Spanned;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.text.style.ForegroundColorSpan;
import android.text.style.ReplacementSpan;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.PopupWindow;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import androidx.core.view.WindowCompat;

import com.google.zxing.integration.android.IntentIntegrator;
import com.google.zxing.integration.android.IntentResult;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.EOFException;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetSocketAddress;
import java.net.InetAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Date;
import java.util.Deque;
import java.util.List;
import java.util.Locale;
import java.util.UUID;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.FutureTask;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

public class MainActivity extends Activity {
    private static final int DISCOVERY_PORT = 41820;
    private static final int REQUEST_FILE = 2001;
    private static final int REQUEST_CAMERA = 2002;
    private static final long MAX_PAYLOAD = 512L * 1024 * 1024;
    private static final int TAB_TEXT = 0;
    private static final int TAB_TASK = 1;
    private static final int TAB_TRANSFER = 2;
    private static final int TAB_RECYCLE_BIN = 3;
    private static final long TASK_PURGE_ARM_DELAY_MS = 900;
    private static final long TASK_PURGE_COOLDOWN_MS = 1500;
    private static final String NOTES_KEY = "notes";
    private static final String RECYCLE_BIN_KEY = "recycleBin";
    private static final String TASKS_KEY = "tasks";
    private static final String DEVICE_ID_KEY = "deviceId";
    private static final String PAIRED_WINDOWS_ID_KEY = "pairedWindowsId";
    private static final String PAIRED_WINDOWS_NAME_KEY = "pairedWindowsName";
    private static final String PAIR_KEY_KEY = "pairKey";
    private int BACKGROUND;
    private int SURFACE;
    private int SURFACE_ACTIVE;
    private int PRIMARY;
    private int TEXT_PRIMARY;
    private int TEXT_SECONDARY;
    private int NOTE_BACKGROUND;
    private int NOTE_CARD;
    private int NOTE_PRIMARY;
    private int NOTE_SECONDARY;
    private int NOTE_EDITOR_BACKGROUND;
    private int NOTE_EDITOR_TEXT;
    private int NOTE_EDITOR_SECONDARY;
    private String visualTheme;
    private final ExecutorService executor = Executors.newCachedThreadPool();
    private final Object sendLock = new Object();
    private final Object autoDiscoveryLock = new Object();
    private String deviceId;
    private TextView statusText;
    private TextView peerText;
    private TextView logText;
    private EditText messageEdit;
    private Button sendTextButton;
    private Button sendFileButton;
    private Button disconnectButton;
    private Button syncAllButton;
    private FrameLayout contentRoot;
    private Button textTabButton;
    private Button taskTabButton;
    private Button transferTabButton;
    private int activeTab = TAB_TEXT;
    private int recycleBinReturnTab = TAB_TEXT;
    private volatile boolean connected;
    private String connectionStatus = "尚未连接";
    private String connectionDetail = "请先在电脑端打开应用";
    private final StringBuilder transferLog = new StringBuilder("等待扫码或自动连接……");
    private final List<Note> notes = new ArrayList<>();
    private final List<Note> recycleBin = new ArrayList<>();
    private final List<WorkoutTask> tasks = new ArrayList<>();
    private WorkoutTask activeWorkoutTask;
    private boolean permanentTaskDeleteDialogVisible;
    private long lastTaskPurgedAt;
    private boolean completedTasksExpanded;
    private LinearLayout completedTasksContainer;
    private CountDownTimer restTimer;
    private TextView restTimerText;
    private long restSecondsRemaining;
    private TextView workoutStateText;
    private TextView workoutProgressText;
    private android.widget.ProgressBar workoutProgress;
    private Button finishWorkoutButton;
    private Note editingNote;
    private ChecklistEditText noteEditor;
    private TextView undoButton;
    private TextView redoButton;
    private TextView fontSizeValue;
    private volatile Socket socket;
    private volatile DataInputStream input;
    private volatile DataOutputStream output;
    private volatile boolean autoDiscoveryRunning;
    private boolean autoConnectInProgress;
    private volatile boolean suppressAutoReconnect;
    private volatile boolean destroyed;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        WindowCompat.setDecorFitsSystemWindows(getWindow(), true);
        deviceId = getPreferences(MODE_PRIVATE).getString(DEVICE_ID_KEY, "");
        if (deviceId.isEmpty()) {
            deviceId = "android-" + UUID.randomUUID().toString().replace("-", "");
            getPreferences(MODE_PRIVATE).edit().putString(DEVICE_ID_KEY, deviceId).apply();
        }
        applyVisualTheme(getPreferences(MODE_PRIVATE).getString("visualTheme", "Silver"), false);
        loadNotes();
        loadTasks();
        setContentView(buildUi());
        startAutoDiscovery();
    }

    private void applyVisualTheme(String choice, boolean rebuild) {
        visualTheme = choice;
        if ("Midnight".equals(choice)) {
            BACKGROUND = Color.rgb(9, 16, 29); SURFACE = Color.rgb(20, 30, 48);
            SURFACE_ACTIVE = Color.rgb(35, 52, 78); PRIMARY = Color.rgb(105, 178, 255);
            TEXT_PRIMARY = Color.rgb(241, 246, 255); TEXT_SECONDARY = Color.rgb(157, 177, 205);
            NOTE_EDITOR_BACKGROUND = Color.rgb(28, 38, 52);
            NOTE_EDITOR_TEXT = Color.rgb(197, 206, 218);
            NOTE_EDITOR_SECONDARY = Color.rgb(145, 157, 174);
        } else if ("Moss".equals(choice)) {
            BACKGROUND = Color.rgb(12, 19, 16); SURFACE = Color.rgb(23, 34, 28);
            SURFACE_ACTIVE = Color.rgb(42, 58, 47); PRIMARY = Color.rgb(167, 213, 122);
            TEXT_PRIMARY = Color.rgb(242, 248, 238); TEXT_SECONDARY = Color.rgb(170, 188, 164);
            NOTE_EDITOR_BACKGROUND = Color.rgb(32, 42, 36);
            NOTE_EDITOR_TEXT = Color.rgb(200, 208, 200);
            NOTE_EDITOR_SECONDARY = Color.rgb(148, 160, 149);
        } else {
            visualTheme = "Silver";
            BACKGROUND = Color.rgb(243, 244, 246); SURFACE = Color.rgb(255, 255, 255);
            SURFACE_ACTIVE = Color.rgb(236, 238, 242); PRIMARY = Color.rgb(84, 89, 103);
            TEXT_PRIMARY = Color.rgb(38, 42, 51); TEXT_SECONDARY = Color.rgb(89, 97, 112);
            NOTE_EDITOR_BACKGROUND = Color.rgb(250, 250, 252);
            NOTE_EDITOR_TEXT = Color.rgb(45, 49, 58);
            NOTE_EDITOR_SECONDARY = Color.rgb(108, 114, 128);
        }
        NOTE_BACKGROUND = BACKGROUND; NOTE_CARD = SURFACE; NOTE_PRIMARY = TEXT_PRIMARY; NOTE_SECONDARY = TEXT_SECONDARY;
        getPreferences(MODE_PRIVATE).edit().putString("visualTheme", visualTheme).apply();
        getWindow().setStatusBarColor(BACKGROUND);
        getWindow().setNavigationBarColor(BACKGROUND);
        WindowCompat.getInsetsController(getWindow(), getWindow().getDecorView())
                .setAppearanceLightStatusBars("Silver".equals(visualTheme));
        WindowCompat.getInsetsController(getWindow(), getWindow().getDecorView())
                .setAppearanceLightNavigationBars("Silver".equals(visualTheme));
        if (rebuild) setContentView(buildUi());
    }

    private void showThemeChooser() {
        String[] choices = {"银白", "午夜蓝", "苔原绿"};
        int selected = "Midnight".equals(visualTheme) ? 1 : "Moss".equals(visualTheme) ? 2 : 0;
        Dialog dialog = new Dialog(this);
        LinearLayout panel = dialogPanel("控制中心", "连接状态、手动同步、回收站与 App 风格");
        LinearLayout connection = new LinearLayout(this);
        connection.setOrientation(LinearLayout.VERTICAL);
        connection.setPadding(dp(16), dp(14), dp(16), dp(14));
        connection.setBackground(rounded(SURFACE_ACTIVE, 14));
        connection.addView(text(connected ? "●  设备已连接" : "○  尚未连接", 15,
                connected ? connectionColor() : TEXT_PRIMARY), matchWrap());
        TextView detail = text(connectionDetail, 12, TEXT_SECONDARY);
        detail.setPadding(0, dp(6), 0, 0);
        connection.addView(detail, matchWrap());
        panel.addView(connection, matchWrap());

        syncAllButton = dialogButton("同步全部", false);
        syncAllButton.setEnabled(connected);
        syncAllButton.setTextColor(connected ? BACKGROUND : TEXT_SECONDARY);
        syncAllButton.setBackground(rounded(connected ? PRIMARY : SURFACE_ACTIVE, 14));
        syncAllButton.setOnClickListener(v -> {
            dialog.dismiss();
            syncAll();
        });
        LinearLayout.LayoutParams syncParams = matchWrap();
        syncParams.height = dp(48);
        syncParams.setMargins(0, dp(12), 0, dp(14));
        panel.addView(syncAllButton, syncParams);
        Button recycleBinButton = dialogButton("回收站", false);
        recycleBinButton.setOnClickListener(v -> {
            dialog.dismiss();
            showTab(TAB_RECYCLE_BIN);
        });
        LinearLayout.LayoutParams recycleBinParams = matchWrap();
        recycleBinParams.height = dp(48);
        recycleBinParams.setMargins(0, 0, 0, dp(14));
        panel.addView(recycleBinButton, recycleBinParams);
        TextView themeTitle = text("App 风格", 13, TEXT_SECONDARY);
        themeTitle.setPadding(0, 0, 0, dp(4));
        panel.addView(themeTitle, matchWrap());
        for (int i = 0; i < choices.length; i++) {
            int choice = i;
            TextView option = text((i == selected ? "●  " : "○  ") + choices[i], 16,
                    i == selected ? PRIMARY : TEXT_PRIMARY);
            option.setGravity(Gravity.CENTER_VERTICAL);
            option.setPadding(dp(16), 0, dp(16), 0);
            option.setBackground(rounded(i == selected ? SURFACE_ACTIVE : Color.TRANSPARENT, 14));
            option.setOnClickListener(v -> {
                dialog.dismiss();
                applyVisualTheme(choice == 1 ? "Midnight" : choice == 2 ? "Moss" : "Silver", true);
            });
            LinearLayout.LayoutParams optionParams = matchWrap();
            optionParams.height = dp(52);
            optionParams.setMargins(0, dp(4), 0, 0);
            panel.addView(option, optionParams);
        }
        Button cancel = dialogButton("取消", false);
        cancel.setOnClickListener(v -> dialog.dismiss());
        LinearLayout.LayoutParams cancelParams = matchWrap();
        cancelParams.setMargins(0, dp(12), 0, 0);
        panel.addView(cancel, cancelParams);
        showThemedDialog(dialog, panel);
    }

    private View buildUi() {
        int pad = dp(20);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(BACKGROUND);
        root.setFitsSystemWindows(true);

        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        header.setPadding(pad, dp(22), pad, dp(14));
        TextView title = text("V", 30, TEXT_PRIMARY);
        title.setTypeface(null, android.graphics.Typeface.NORMAL);
        title.setContentDescription("打开控制中心");
        title.setOnClickListener(v -> showThemeChooser());
        header.addView(title, weighted());
        FrameLayout scan = new FrameLayout(this);
        scan.setContentDescription("扫码连接设备");
        scan.setBackground(rounded(SURFACE, 16));
        scan.addView(new ScanIconView(this), new FrameLayout.LayoutParams(dp(26), dp(26), Gravity.CENTER));
        scan.setOnClickListener(v -> startQrScan());
        header.addView(scan, fixed(dp(48), dp(48)));
        root.addView(header, matchWrap());

        contentRoot = new FrameLayout(this);
        root.addView(contentRoot, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));

        LinearLayout navigation = new LinearLayout(this);
        navigation.setGravity(Gravity.CENTER);
        navigation.setPadding(dp(8), dp(6), dp(8), dp(6));
        navigation.setBackground(rounded(SURFACE, 20));
        textTabButton = navigationButton("文本", TAB_TEXT);
        taskTabButton = navigationButton("任务", TAB_TASK);
        transferTabButton = navigationButton("传输", TAB_TRANSFER);
        navigation.addView(textTabButton, weighted(dp(52)));
        navigation.addView(taskTabButton, weighted(dp(52)));
        navigation.addView(transferTabButton, weighted(dp(52)));
        LinearLayout.LayoutParams navParams = matchWrap();
        navParams.setMargins(dp(16), 0, dp(16), dp(16));
        root.addView(navigation, navParams);

        showTab(activeTab);
        return root;
    }

    private void showTab(int tab) {
        if (editingNote != null) saveEditingNote();
        if (tab == TAB_RECYCLE_BIN && activeTab != TAB_RECYCLE_BIN) recycleBinReturnTab = activeTab;
        activeTab = tab;
        contentRoot.removeAllViews();
        if (tab == TAB_TEXT) {
            contentRoot.addView(buildNotesWorkspace());
        } else if (tab == TAB_TASK) {
            contentRoot.addView(buildTaskWorkspace());
        } else if (tab == TAB_TRANSFER) {
            contentRoot.addView(buildTransferPage());
        } else {
            contentRoot.addView(buildRecycleBinPage());
        }
        updateNavigation();
    }

    private View buildNotesWorkspace() {
        LinearLayout page = pageLayout();
        page.setBackgroundColor(NOTE_BACKGROUND);
        LinearLayout toolbar = new LinearLayout(this);
        toolbar.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout titleStack = new LinearLayout(this);
        titleStack.setOrientation(LinearLayout.VERTICAL);
        titleStack.addView(text("文本", 30, NOTE_PRIMARY));
        TextView count = text(notes.isEmpty() ? "开始记录你的想法" : notes.size() + " 条本地记录", 13, NOTE_SECONDARY);
        count.setPadding(0, dp(3), 0, 0);
        titleStack.addView(count);
        toolbar.addView(titleStack, weighted());
        Button create = button("新建");
        create.setTextColor(BACKGROUND);
        create.setBackground(rounded(PRIMARY, 16));
        create.setOnClickListener(v -> v.post(() -> openNoteEditor(
                new Note(UUID.randomUUID().toString(), "未命名文本", "", 18, System.currentTimeMillis()))));
        toolbar.addView(create, fixed(dp(68), dp(44)));
        page.addView(toolbar, matchWrap());

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        LinearLayout cards = new LinearLayout(this);
        cards.setOrientation(LinearLayout.VERTICAL);
        if (notes.isEmpty()) {
            TextView empty = text("还没有文本记录\n\n点击右上角“新建”，记录此刻。", 16, NOTE_SECONDARY);
            empty.setGravity(Gravity.CENTER);
            empty.setPadding(0, dp(96), 0, 0);
            cards.addView(empty, matchWrap());
        } else {
            for (Note note : notes) cards.addView(noteCard(note, false));
        }
        scroll.addView(cards);
        page.addView(scroll, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));
        return page;
    }

    private View buildTaskWorkspace() {
        LinearLayout page = pageLayout();
        LinearLayout heading = new LinearLayout(this);
        heading.setOrientation(LinearLayout.VERTICAL);
        heading.addView(text("任务", 30, TEXT_PRIMARY));
        int active = 0;
        int visible = 0;
        for (WorkoutTask task : tasks) {
            if (isTaskHidden(task)) continue;
            visible++;
            if (!isTaskFinished(task)) active++;
        }
        TextView subtitle = text(active + " 项进行中", 13, TEXT_SECONDARY);
        subtitle.setPadding(0, dp(3), 0, dp(18));
        heading.addView(subtitle);
        page.addView(heading, matchWrap());

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        LinearLayout cards = new LinearLayout(this);
        cards.setOrientation(LinearLayout.VERTICAL);
        if (visible == 0) {
            TextView empty = text("还没有训练任务\n\n在电脑端确认计划后，点击 V → 同步全部。", 16, TEXT_SECONDARY);
            empty.setGravity(Gravity.CENTER);
            empty.setPadding(0, dp(96), 0, 0);
            cards.addView(empty, matchWrap());
        } else {
            TextView activeHeading = text("进行中 · " + active, 14, TEXT_SECONDARY);
            activeHeading.setPadding(0, 0, 0, dp(10));
            cards.addView(activeHeading, matchWrap());
            for (WorkoutTask task : tasks) if (!isTaskHidden(task) && !isTaskFinished(task)) cards.addView(taskCard(task));
            int completed = visible - active;
            cards.addView(completedTasksHeader(completed));
            completedTasksContainer = new LinearLayout(this);
            completedTasksContainer.setOrientation(LinearLayout.VERTICAL);
            List<WorkoutTask> completedTasks = new ArrayList<>();
            for (WorkoutTask task : tasks) if (isTaskFinished(task)) completedTasks.add(task);
            completedTasks.sort((left, right) -> Long.compare(
                    right.completedAt > 0 ? right.completedAt : right.updatedAt,
                    left.completedAt > 0 ? left.completedAt : left.updatedAt));
            for (WorkoutTask task : completedTasks) completedTasksContainer.addView(compactCompletedTaskCard(task));
            completedTasksContainer.setVisibility(completedTasksExpanded ? View.VISIBLE : View.GONE);
            cards.addView(completedTasksContainer, matchWrap());
        }
        scroll.addView(cards);
        page.addView(scroll, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));
        return page;
    }

    private View completedTasksHeader(int completed) {
        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        header.setPadding(dp(14), dp(12), dp(14), dp(12));
        header.setBackground(rounded(SURFACE, 14));
        header.addView(text("已完成事项", 15, TEXT_PRIMARY), weighted());
        TextView state = text(completed + " 项" + (completed > 0 ? completedTasksExpanded ? "  ⌃" : "  ⌄" : ""),
                13, TEXT_SECONDARY);
        state.setGravity(Gravity.END | Gravity.CENTER_VERTICAL);
        header.addView(state, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        if (completed > 0) {
            header.setOnClickListener(v -> {
                completedTasksExpanded = !completedTasksExpanded;
                state.setText(completed + " 项" + (completedTasksExpanded ? "  ⌃" : "  ⌄"));
                if (completedTasksContainer != null) {
                    completedTasksContainer.setVisibility(completedTasksExpanded ? View.VISIBLE : View.GONE);
                }
            });
        }
        LinearLayout.LayoutParams params = matchWrap();
        params.setMargins(0, dp(4), 0, dp(8));
        header.setLayoutParams(params);
        return header;
    }

    private View compactCompletedTaskCard(WorkoutTask task) {
        LinearLayout card = new LinearLayout(this);
        card.setGravity(Gravity.CENTER_VERTICAL);
        card.setPadding(dp(14), dp(9), dp(12), dp(9));
        card.setBackground(rounded(SURFACE, 12));
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        TextView title = text(task.title, 15, TEXT_PRIMARY);
        title.setSingleLine(true);
        title.setEllipsize(TextUtils.TruncateAt.END);
        content.addView(title, matchWrap());
        long finishedAt = task.completedAt > 0 ? task.completedAt : task.updatedAt;
        String date = new SimpleDateFormat("yyyy-MM-dd", Locale.CHINA).format(new Date(finishedAt));
        content.addView(text(date + " · " + completedSets(task) + "/" + totalSets(task) + " 组", 12, TEXT_SECONDARY), matchWrap());
        card.addView(content, weighted());
        card.addView(text("查看 ›", 12, PRIMARY), new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        card.setOnClickListener(v -> openWorkoutTask(task));
        card.setOnLongClickListener(v -> {
            showDeleteTaskDialog(task);
            return true;
        });
        LinearLayout.LayoutParams params = fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(66));
        params.setMargins(0, 0, 0, dp(6));
        card.setLayoutParams(params);
        return card;
    }

    private View taskCard(WorkoutTask task) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(18), dp(16), dp(18), dp(16));
        card.setBackground(rounded(SURFACE, 18));
        int total = totalSets(task);
        int done = completedSets(task);
        TextView label = text(isTaskFinished(task) ? "委托完成" : done > 0 ? "进行中" : "今日委托", 13, PRIMARY);
        card.addView(label, matchWrap());
        TextView title = text(task.title, 21, TEXT_PRIMARY);
        title.setPadding(0, dp(10), 0, 0);
        card.addView(title, matchWrap());
        String metadata = "预计 " + task.estimatedMinutes + " 分钟 · " + total + " 组";
        if (!task.scheduledDate.isEmpty()) metadata = task.scheduledDate + " · " + metadata;
        TextView meta = text(metadata, 13, TEXT_SECONDARY);
        meta.setPadding(0, dp(5), 0, 0);
        card.addView(meta, matchWrap());
        TextView progressText = text(done + " / " + total + " 组", 15, PRIMARY);
        progressText.setPadding(0, dp(16), 0, dp(6));
        card.addView(progressText, matchWrap());
        android.widget.ProgressBar progress = new android.widget.ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progress.setMax(Math.max(1, total));
        progress.setProgress(done);
        progress.setProgressTintList(ColorStateList.valueOf(PRIMARY));
        progress.setProgressBackgroundTintList(ColorStateList.valueOf(SURFACE_ACTIVE));
        card.addView(progress, fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(5)));
        StringBuilder names = new StringBuilder();
        for (int i = 0; i < Math.min(3, task.exercises.size()); i++) {
            if (i > 0) names.append("  ·  ");
            names.append(task.exercises.get(i).name);
        }
        TextView exercises = text(names.toString(), 13, TEXT_SECONDARY);
        exercises.setPadding(0, dp(14), 0, dp(12));
        card.addView(exercises, matchWrap());
        Button action = button(isTaskFinished(task) ? "查看记录" : done > 0 ? "继续执行" : "开始训练");
        action.setTextColor(BACKGROUND);
        action.setBackground(rounded(PRIMARY, 15));
        action.setOnClickListener(v -> openWorkoutTask(task));
        action.setOnLongClickListener(v -> {
            showDeleteTaskDialog(task);
            return true;
        });
        card.addView(action, fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(46)));
        card.setOnClickListener(v -> openWorkoutTask(task));
        card.setOnLongClickListener(v -> {
            showDeleteTaskDialog(task);
            return true;
        });
        LinearLayout.LayoutParams params = matchWrap();
        params.setMargins(0, 0, 0, dp(12));
        card.setLayoutParams(params);
        return card;
    }

    private void openWorkoutTask(WorkoutTask task) {
        activeWorkoutTask = task;
        if (isTaskFinished(task)) setContentView(buildWorkoutSummary(task));
        else setContentView(buildWorkoutTaskView(task));
    }

    private View buildWorkoutTaskView(WorkoutTask task) {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(BACKGROUND);
        root.setFitsSystemWindows(true);
        LinearLayout header = new LinearLayout(this);
        header.setGravity(Gravity.CENTER_VERTICAL);
        header.setPadding(dp(14), dp(16), dp(14), dp(10));
        Button back = button("‹");
        back.setTextSize(30);
        back.setBackgroundColor(Color.TRANSPARENT);
        back.setOnClickListener(v -> returnToTaskWorkspace());
        header.addView(back, fixed(dp(46), dp(46)));
        TextView title = text(task.title, 21, TEXT_PRIMARY);
        title.setGravity(Gravity.CENTER);
        header.addView(title, weighted());
        Button menu = button("⋮");
        menu.setTextSize(24);
        menu.setBackgroundColor(Color.TRANSPARENT);
        menu.setOnClickListener(v -> showTaskNotes(task));
        header.addView(menu, fixed(dp(46), dp(46)));
        root.addView(header, matchWrap());

        ScrollView scroll = new ScrollView(this);
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(dp(16), dp(8), dp(16), dp(18));
        int total = totalSets(task);
        int done = completedSets(task);
        TextView commission = text("委托 #" + task.id, 13, TEXT_SECONDARY);
        content.addView(commission, matchWrap());
        LinearLayout progressRow = new LinearLayout(this);
        progressRow.setGravity(Gravity.CENTER_VERTICAL);
        workoutStateText = text(done == 0 ? "等待执行" : "进行中", 14, PRIMARY);
        workoutStateText.setPadding(dp(12), dp(7), dp(12), dp(7));
        workoutStateText.setBackground(rounded(SURFACE_ACTIVE, 14));
        progressRow.addView(workoutStateText, weighted());
        workoutProgressText = text(done + " / " + total + " 组", 17, PRIMARY);
        workoutProgressText.setGravity(Gravity.END);
        progressRow.addView(workoutProgressText, weighted());
        LinearLayout.LayoutParams progressRowParams = matchWrap();
        progressRowParams.setMargins(0, dp(10), 0, dp(8));
        content.addView(progressRow, progressRowParams);
        workoutProgress = new android.widget.ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        workoutProgress.setMax(Math.max(1, total));
        workoutProgress.setProgress(done);
        workoutProgress.setProgressTintList(ColorStateList.valueOf(PRIMARY));
        workoutProgress.setProgressBackgroundTintList(ColorStateList.valueOf(SURFACE_ACTIVE));
        content.addView(workoutProgress, fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(6)));

        for (WorkoutExercise exercise : task.exercises) content.addView(exerciseCard(task, exercise));
        content.addView(addExerciseButton(task));
        content.addView(taskFeedbackCard(task));
        scroll.addView(content);
        root.addView(scroll, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));

        LinearLayout footer = new LinearLayout(this);
        footer.setOrientation(LinearLayout.VERTICAL);
        footer.setPadding(dp(16), dp(10), dp(16), dp(16));
        footer.setBackground(rounded(SURFACE, 20));
        LinearLayout timerRow = new LinearLayout(this);
        timerRow.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout timerTextStack = new LinearLayout(this);
        timerTextStack.setOrientation(LinearLayout.VERTICAL);
        timerTextStack.addView(text("组间休息", 13, TEXT_SECONDARY));
        restTimerText = text(formatTimer(restSecondsRemaining), 30, TEXT_PRIMARY);
        timerTextStack.addView(restTimerText);
        timerRow.addView(timerTextStack, weighted());
        Button skip = button("跳过");
        skip.setTextColor(PRIMARY);
        skip.setOnClickListener(v -> cancelRestTimer());
        timerRow.addView(skip, fixed(dp(82), dp(44)));
        footer.addView(timerRow, matchWrap());
        finishWorkoutButton = button("完成本次训练");
        boolean canFinish = total > 0 && done > 0;
        finishWorkoutButton.setEnabled(canFinish);
        finishWorkoutButton.setTextColor(canFinish ? BACKGROUND : TEXT_SECONDARY);
        finishWorkoutButton.setBackground(rounded(canFinish ? PRIMARY : SURFACE_ACTIVE, 15));
        finishWorkoutButton.setOnClickListener(v -> requestCompleteWorkoutTask(task));
        LinearLayout.LayoutParams finishParams = fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(50));
        finishParams.setMargins(0, dp(8), 0, 0);
        footer.addView(finishWorkoutButton, finishParams);
        root.addView(footer, matchWrap());
        return root;
    }

    private View exerciseCard(WorkoutTask task, WorkoutExercise exercise) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(16), dp(15), dp(16), dp(10));
        card.setBackground(rounded(SURFACE, 18));
        TextView name = text(exercise.name + (exercise.isAdditional ? " · 临时新增" : ""), 19, TEXT_PRIMARY);
        card.addView(name, matchWrap());
        TextView target = text(exercise.sets.size() + " 组 · 休息 " + exercise.restSeconds + " 秒", 13, TEXT_SECONDARY);
        target.setPadding(0, dp(5), 0, dp(8));
        card.addView(target, matchWrap());
        TextView feedback = text(exerciseFeedbackText(exercise), 13, TEXT_SECONDARY);
        Runnable refresh = () -> feedback.setText(exerciseFeedbackText(exercise));
        for (WorkoutSet set : exercise.sets) card.addView(workoutSetRow(task, exercise, set, refresh));
        feedback.setPadding(0, dp(10), 0, dp(6));
        card.addView(feedback, matchWrap());
        Button review = dialogButton("记录末组反馈", false);
        review.setOnClickListener(v -> {
            WorkoutSet last = lastCompletedSet(exercise);
            if (last == null) {
                Toast.makeText(this, "完成一组后可记录反馈；其他组可点击组号填写", Toast.LENGTH_SHORT).show();
                return;
            }
            showSetFeedback(task, exercise, last, refresh);
        });
        card.addView(review, fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(44)));
        Button skipReason = button("未做 / 少做原因");
        skipReason.setTextColor(TEXT_SECONDARY);
        skipReason.setBackgroundColor(Color.TRANSPARENT);
        skipReason.setOnClickListener(v -> showSkipReason(task, exercise, refresh));
        card.addView(skipReason, fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(44)));
        if (!exercise.notes.isEmpty()) {
            TextView note = text(exercise.notes, 12, TEXT_SECONDARY);
            note.setPadding(0, dp(8), 0, dp(4));
            card.addView(note, matchWrap());
        }
        LinearLayout.LayoutParams params = matchWrap();
        params.setMargins(0, dp(16), 0, 0);
        card.setLayoutParams(params);
        return card;
    }

    private View workoutSetRow(WorkoutTask task, WorkoutExercise exercise, WorkoutSet set, Runnable refresh) {
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(0, dp(7), 0, dp(7));
        Button check = button(set.completed ? "✓" : "□");
        check.setTextSize(20);
        check.setTextColor(set.completed ? BACKGROUND : TEXT_SECONDARY);
        check.setBackground(rounded(set.completed ? PRIMARY : Color.TRANSPARENT, 8));
        TextView label = text("第 " + set.index + " 组", 14, set.completed ? TEXT_SECONDARY : TEXT_PRIMARY);
        label.setPadding(dp(10), 0, dp(8), 0);
        label.setGravity(Gravity.CENTER_VERTICAL);
        label.setOnClickListener(v -> {
            if (set.completed) showSetFeedback(task, exercise, set, refresh);
            else Toast.makeText(this, "完成这一组后可填写反馈", Toast.LENGTH_SHORT).show();
        });
        label.setContentDescription("第 " + set.index + " 组，点击记录反馈");
        row.addView(label, new LinearLayout.LayoutParams(dp(72), dp(44)));
        EditText weight = workoutValueEditor(formatDecimal(set.actualWeight > 0 ? set.actualWeight : set.targetWeight));
        EditText reps = workoutValueEditor(String.valueOf(set.actualReps > 0 ? set.actualReps : set.targetReps));
        row.addView(workoutValueField(weight, "KG"), weighted(dp(42)));
        row.addView(workoutValueField(reps, "次"), weighted(dp(42)));
        LinearLayout.LayoutParams checkParams = fixed(dp(42), dp(42));
        checkParams.setMargins(dp(8), 0, 0, 0);
        row.addView(check, checkParams);
        weight.addTextChangedListener(valueWatcher(() -> {
            set.actualWeight = parseDouble(weight.getText().toString(), set.targetWeight);
            touchTask(task);
        }));
        reps.addTextChangedListener(valueWatcher(() -> {
            set.actualReps = (int) parseDouble(reps.getText().toString(), set.targetReps);
            touchTask(task);
        }));
        check.setOnClickListener(v -> {
            set.actualWeight = parseDouble(weight.getText().toString(), set.targetWeight);
            set.actualReps = (int) parseDouble(reps.getText().toString(), set.targetReps);
            set.completed = !set.completed;
            set.completedAt = set.completed ? System.currentTimeMillis() : 0;
            if (task.startedAt == 0) task.startedAt = System.currentTimeMillis();
            if (!isTaskFinished(task)) task.status = "active";
            touchTask(task);
            boolean startRest = !isTaskFinished(task) && set.completed && completedSets(task) < totalSets(task);
            cancelRestTimer();
            check.setText(set.completed ? "✓" : "□");
            check.setTextColor(set.completed ? BACKGROUND : TEXT_SECONDARY);
            check.setBackground(rounded(set.completed ? PRIMARY : Color.TRANSPARENT, 8));
            label.setTextColor(set.completed ? TEXT_SECONDARY : TEXT_PRIMARY);
            updateWorkoutProgress(task);
            if (startRest) startRestTimer(exercise.restSeconds);
            refresh.run();
            if (set.completed && exercise.sets.stream().allMatch(item -> item.completed)) {
                showSetFeedback(task, exercise, lastCompletedSet(exercise), refresh);
            }
        });
        return row;
    }

    private void updateWorkoutProgress(WorkoutTask task) {
        int total = totalSets(task);
        int done = completedSets(task);
        if (workoutStateText != null) workoutStateText.setText(done == 0 ? "等待执行" : "进行中");
        if (workoutProgressText != null) workoutProgressText.setText(done + " / " + total + " 组");
        if (workoutProgress != null) workoutProgress.setProgress(done);
        if (finishWorkoutButton != null) {
            boolean canFinish = total > 0 && done > 0;
            finishWorkoutButton.setEnabled(canFinish);
            finishWorkoutButton.setTextColor(canFinish ? BACKGROUND : TEXT_SECONDARY);
            finishWorkoutButton.setBackground(rounded(canFinish ? PRIMARY : SURFACE_ACTIVE, 15));
        }
    }

    private LinearLayout workoutValueField(EditText edit, String suffix) {
        LinearLayout field = new LinearLayout(this);
        field.setGravity(Gravity.CENTER_VERTICAL);
        field.setBackground(rounded(SURFACE_ACTIVE, 10));
        field.addView(edit, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, 1f));
        TextView unit = text("/" + suffix, 13, TEXT_SECONDARY);
        unit.setGravity(Gravity.CENTER_VERTICAL);
        unit.setPadding(0, 0, dp(6), 0);
        field.addView(unit, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.MATCH_PARENT));
        return field;
    }

    private EditText workoutValueEditor(String value) {
        EditText edit = new EditText(this);
        configureInputColors(edit);
        edit.setText(value);
        edit.setSingleLine(true);
        edit.setSelectAllOnFocus(true);
        edit.setTextSize(14);
        edit.setGravity(Gravity.CENTER_VERTICAL | Gravity.END);
        edit.setTextColor(TEXT_PRIMARY);
        edit.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_DECIMAL);
        edit.setBackgroundColor(Color.TRANSPARENT);
        edit.setPadding(dp(6), 0, dp(2), 0);
        return edit;
    }

    private TextWatcher valueWatcher(Runnable changed) {
        return new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) { }
            @Override public void afterTextChanged(Editable s) { changed.run(); }
        };
    }

    private void requestCompleteWorkoutTask(WorkoutTask task) {
        int done = completedSets(task);
        int total = totalSets(task);
        if (done >= total) {
            completeWorkoutTask(task);
            return;
        }
        Dialog dialog = new Dialog(this);
        LinearLayout panel = dialogPanel("结束本次训练？",
                "当前已完成 " + done + "/" + total + " 组，剩余 " + (total - done)
                        + " 组将保留为未完成。仍可按当前进度结算本次训练。");
        LinearLayout actions = new LinearLayout(this);
        actions.setGravity(Gravity.END);
        Button cancel = dialogButton("继续训练", false);
        cancel.setOnClickListener(v -> dialog.dismiss());
        Button confirm = dialogButton("按当前进度结束", false);
        confirm.setTextColor(BACKGROUND);
        confirm.setBackground(rounded(PRIMARY, 12));
        confirm.setOnClickListener(v -> {
            dialog.dismiss();
            completeWorkoutTask(task);
        });
        LinearLayout.LayoutParams cancelParams = fixed(dp(96), dp(44));
        cancelParams.setMargins(dp(8), dp(16), 0, 0);
        actions.addView(cancel, cancelParams);
        LinearLayout.LayoutParams confirmParams = fixed(dp(142), dp(44));
        confirmParams.setMargins(dp(8), dp(16), 0, 0);
        actions.addView(confirm, confirmParams);
        panel.addView(actions, matchWrap());
        showThemedDialog(dialog, panel);
    }

    private void completeWorkoutTask(WorkoutTask task) {
        cancelRestTimer();
        task.status = "completed";
        task.completedAt = System.currentTimeMillis();
        task.updatedAt = task.completedAt;
        saveTasks();
        setContentView(buildWorkoutSummary(task));
        showTaskFeedback(task, () -> setContentView(buildWorkoutSummary(task)));
    }

    private void showTaskNotes(WorkoutTask task) {
        Dialog dialog = new Dialog(this);
        LinearLayout panel = dialogPanel("任务说明", task.notes.isEmpty() ? "本任务没有额外说明。" : task.notes);
        Button close = dialogButton("关闭", false);
        close.setOnClickListener(v -> dialog.dismiss());
        panel.addView(close, fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(46)));
        showThemedDialog(dialog, panel);
    }

    private View buildWorkoutSummary(WorkoutTask task) {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(BACKGROUND);
        root.setFitsSystemWindows(true);
        root.setPadding(dp(20), dp(18), dp(20), dp(20));
        Button back = button("‹");
        back.setTextSize(30);
        back.setBackgroundColor(Color.TRANSPARENT);
        back.setOnClickListener(v -> returnToTaskWorkspace());
        root.addView(back, fixed(dp(46), dp(46)));
        ScrollView scroll = new ScrollView(this);
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        scroll.addView(content);
        root.addView(scroll, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));
        TextView heading = text("训练结算", 21, TEXT_PRIMARY);
        heading.setGravity(Gravity.CENTER);
        content.addView(heading, matchWrap());
        TextView check = text("✓", 58, PRIMARY);
        check.setGravity(Gravity.CENTER);
        check.setPadding(0, dp(24), 0, dp(4));
        content.addView(check, matchWrap());
        int completedCount = completedSets(task);
        int totalCount = totalSets(task);
        int completionPercent = totalCount > 0 ? completedCount * 100 / totalCount : 0;
        TextView completed = text(completedCount == totalCount ? "委托完成" : "委托已结算", 30, TEXT_PRIMARY);
        completed.setGravity(Gravity.CENTER);
        content.addView(completed, matchWrap());
        TextView taskName = text(task.title + "\n完成度 " + completionPercent + "%", 16, TEXT_SECONDARY);
        taskName.setGravity(Gravity.CENTER);
        taskName.setPadding(0, dp(8), 0, dp(22));
        content.addView(taskName, matchWrap());

        long durationMinutes = task.startedAt > 0 && task.completedAt > task.startedAt
                ? Math.max(1, (task.completedAt - task.startedAt) / 60000) : task.estimatedMinutes;
        double volume = workoutVolume(task);
        LinearLayout metrics = new LinearLayout(this);
        metrics.setOrientation(LinearLayout.VERTICAL);
        metrics.setPadding(dp(18), dp(16), dp(18), dp(16));
        metrics.setBackground(rounded(SURFACE, 18));
        metrics.addView(text("训练时长    " + durationMinutes + " 分钟", 16, TEXT_PRIMARY), matchWrap());
        TextView sets = text("完成组数    " + completedSets(task) + " 组", 16, TEXT_PRIMARY);
        sets.setPadding(0, dp(12), 0, 0);
        metrics.addView(sets, matchWrap());
        TextView capacity = text("训练容量    " + formatDecimal(volume) + " kg", 16, TEXT_PRIMARY);
        capacity.setPadding(0, dp(12), 0, 0);
        metrics.addView(capacity, matchWrap());
        content.addView(metrics, matchWrap());
        content.addView(taskFeedbackCard(task));
        for (WorkoutExercise exercise : task.exercises) content.addView(exerciseCard(task, exercise));
        content.addView(addExerciseButton(task));

        TextView syncNote = text("记录已保存在本机，下一次同步时发送至电脑。", 13, TEXT_SECONDARY);
        syncNote.setPadding(dp(16), dp(14), dp(16), dp(14));
        syncNote.setBackground(rounded(SURFACE, 16));
        LinearLayout.LayoutParams noteParams = matchWrap();
        noteParams.setMargins(0, dp(14), 0, 0);
        content.addView(syncNote, noteParams);
        Button archive = button("保存并归档");
        archive.setTextColor(BACKGROUND);
        archive.setBackground(rounded(PRIMARY, 16));
        archive.setOnClickListener(v -> {
            task.status = "archived";
            task.updatedAt = System.currentTimeMillis();
            saveTasks();
            returnToTaskWorkspace();
        });
        root.addView(archive, fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(52)));
        Button returnButton = button("返回任务");
        returnButton.setTextColor(PRIMARY);
        returnButton.setBackgroundColor(Color.TRANSPARENT);
        returnButton.setOnClickListener(v -> returnToTaskWorkspace());
        root.addView(returnButton, fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(46)));
        return root;
    }

    private static final String[] RIR_VALUES = {null, "0", "1", "2", "3", "4+", "uncertain"};
    private static final String[] RIR_LABELS = {"未记录", "0 次", "1 次", "2 次", "3 次", "4 次以上", "不确定"};
    private static final String[] QUALITY_VALUES = {null, "stable", "compensated", "uncertain"};
    private static final String[] QUALITY_LABELS = {"未记录", "稳定", "后几次幅度缩小或借力", "动作不熟、不确定"};
    private static final String[] DISCOMFORT_VALUES = {null, "none", "present"};
    private static final String[] DISCOMFORT_LABELS = {"未记录", "无", "有"};

    private static String emptyToNull(String value) {
        return value == null || value.trim().isEmpty() ? null : value.trim();
    }

    private static String nullableString(JSONObject item, String key) {
        return item.isNull(key) ? null : emptyToNull(item.optString(key));
    }

    private static String feedbackLabel(String value, String[] values, String[] labels) {
        for (int i = 0; i < values.length; i++) {
            if (java.util.Objects.equals(value, values[i])) return labels[i];
        }
        return value == null ? "未记录" : value;
    }

    private static WorkoutSet lastCompletedSet(WorkoutExercise exercise) {
        WorkoutSet last = null;
        for (WorkoutSet set : exercise.sets) {
            if (set.completed && (last == null || set.completedAt > last.completedAt
                    || (set.completedAt == last.completedAt && set.index > last.index))) last = set;
        }
        return last;
    }

    private static String discomfortText(String value, String notes) {
        return feedbackLabel(value, DISCOMFORT_VALUES, DISCOMFORT_LABELS)
                + (notes == null ? "" : " · " + notes);
    }

    private static String exerciseFeedbackText(WorkoutExercise exercise) {
        StringBuilder result = new StringBuilder();
        for (WorkoutSet set : exercise.sets) {
            if (set.rir == null && set.movementQuality == null && set.discomfort == null && set.discomfortNotes == null) continue;
            if (result.length() > 0) result.append("\n");
            result.append("第 ").append(set.index).append(" 组").append(set.completed ? "" : "（未完成）")
                    .append(" · 余力 ").append(feedbackLabel(set.rir, RIR_VALUES, RIR_LABELS))
                    .append("\n动作：").append(feedbackLabel(set.movementQuality, QUALITY_VALUES, QUALITY_LABELS))
                    .append(" · 不适：").append(discomfortText(set.discomfort, set.discomfortNotes));
        }
        if (result.length() == 0) result.append("动作反馈：未记录");
        result.append("\n未做 / 少做原因：").append(exercise.skipReason == null ? "未记录" : exercise.skipReason);
        result.append("\n其他组反馈可点击组号填写");
        return result.toString();
    }

    private EditText feedbackInput(LinearLayout panel, String title, String value) {
        TextView label = text(title, 14, TEXT_SECONDARY);
        label.setPadding(0, dp(10), 0, dp(5));
        panel.addView(label, matchWrap());
        EditText input = new EditText(this);
        configureInputColors(input);
        input.setText(value == null ? "" : value);
        input.setHint("可留空");
        input.setTextColor(TEXT_PRIMARY);
        input.setHintTextColor(TEXT_SECONDARY);
        input.setTextSize(15);
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE);
        input.setPadding(dp(12), dp(8), dp(12), dp(8));
        input.setBackground(rounded(SURFACE_ACTIVE, 10));
        panel.addView(input, matchWrap());
        return input;
    }

    private Button feedbackChoice(LinearLayout panel, String title, String value, String[] values, String[] labels) {
        Button choice = dialogButton(title + "：" + feedbackLabel(value, values, labels), false);
        choice.setTag(value);
        choice.setTextSize(14);
        choice.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
        choice.setOnClickListener(v -> {
            Dialog dialog = new Dialog(this);
            LinearLayout options = dialogPanel(title, "");
            for (int i = 0; i < values.length; i++) {
                final int selected = i;
                Button option = dialogButton(labels[i], false);
                option.setOnClickListener(item -> {
                    choice.setTag(values[selected]);
                    choice.setText(title + "：" + labels[selected]);
                    dialog.dismiss();
                });
                options.addView(option, fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(46)));
            }
            showFeedbackDialog(dialog, options);
        });
        LinearLayout.LayoutParams params = fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(54));
        params.setMargins(0, dp(8), 0, 0);
        panel.addView(choice, params);
        return choice;
    }

    private void feedbackActions(Dialog dialog, LinearLayout panel, Runnable save) {
        feedbackActions(dialog, panel, save, "保存反馈");
    }

    private void feedbackActions(Dialog dialog, LinearLayout panel, Runnable save, String saveTitle) {
        LinearLayout actions = new LinearLayout(this);
        Button skip = dialogButton("暂不填写", false);
        skip.setOnClickListener(v -> dialog.dismiss());
        actions.addView(skip, weighted(dp(46)));
        Button confirm = dialogButton(saveTitle, false);
        confirm.setBackground(rounded(PRIMARY, 12));
        confirm.setTextColor(BACKGROUND);
        confirm.setOnClickListener(v -> save.run());
        actions.addView(confirm, weighted(dp(46)));
        actions.setPadding(0, dp(16), 0, 0);
        panel.addView(actions, matchWrap());
    }

    private void showFeedbackDialog(Dialog dialog, LinearLayout panel) {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(false);
        scroll.addView(panel);
        showThemedDialog(dialog, scroll);
        if (dialog.getWindow() != null) {
            dialog.getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        }
    }

    private void showSetFeedback(WorkoutTask task, WorkoutExercise exercise, WorkoutSet set, Runnable refresh) {
        Dialog dialog = new Dialog(this);
        LinearLayout panel = dialogPanel(exercise.name + " · 第 " + set.index + " 组",
                "评价这一组，不改动其他组。可跳过或之后补填。");
        Button rir = feedbackChoice(panel, "还能规范做几次", set.rir, RIR_VALUES, RIR_LABELS);
        Button quality = feedbackChoice(panel, "动作保持得怎么样", set.movementQuality, QUALITY_VALUES, QUALITY_LABELS);
        Button discomfort = feedbackChoice(panel, "有没有不舒服", set.discomfort, DISCOMFORT_VALUES, DISCOMFORT_LABELS);
        EditText details = feedbackInput(panel, "不适部位和感受（选择“有”时填写）", set.discomfortNotes);
        feedbackActions(dialog, panel, () -> {
            String selected = (String) discomfort.getTag();
            if ("present".equals(selected) && emptyToNull(details.getText().toString()) == null) {
                details.setError("请填写部位和感受，或暂不填写反馈");
                return;
            }
            set.rir = (String) rir.getTag();
            set.movementQuality = (String) quality.getTag();
            set.discomfort = selected;
            set.discomfortNotes = "present".equals(selected) ? emptyToNull(details.getText().toString()) : null;
            touchTask(task);
            refresh.run();
            dialog.dismiss();
        });
        showFeedbackDialog(dialog, panel);
    }

    private LinearLayout taskFeedbackCard(WorkoutTask task) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(16), dp(14), dp(16), dp(14));
        card.setBackground(rounded(SURFACE, 18));
        card.addView(text("整次训练反馈", 18, TEXT_PRIMARY), matchWrap());
        TextView summary = text(taskFeedbackText(task), 14, TEXT_SECONDARY);
        summary.setPadding(0, dp(8), 0, dp(8));
        card.addView(summary, matchWrap());
        Button edit = dialogButton("填写 / 修改整体反馈", false);
        edit.setOnClickListener(v -> showTaskFeedback(task, () -> summary.setText(taskFeedbackText(task))));
        card.addView(edit, fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(46)));
        LinearLayout.LayoutParams params = matchWrap();
        params.setMargins(0, dp(16), 0, dp(8));
        card.setLayoutParams(params);
        return card;
    }

    private static String taskFeedbackText(WorkoutTask task) {
        return "整体费力：" + (task.effort == null ? "未记录" : task.effort)
                + "\n不适：" + discomfortText(task.discomfort, task.discomfortNotes)
                + "\n备注：" + (task.feedbackNotes == null ? "未记录" : task.feedbackNotes);
    }

    private void showTaskFeedback(WorkoutTask task, Runnable refresh) {
        Dialog dialog = new Dialog(this);
        LinearLayout panel = dialogPanel("整次训练反馈", "可全部或部分留空；之后仍可补填。");
        EditText effort = feedbackInput(panel, "整体费力程度（按自己的感受描述）", task.effort);
        Button discomfort = feedbackChoice(panel, "有没有不舒服", task.discomfort, DISCOMFORT_VALUES, DISCOMFORT_LABELS);
        EditText details = feedbackInput(panel, "不适部位和感受（选择“有”时填写）", task.discomfortNotes);
        EditText notes = feedbackInput(panel, "自由备注", task.feedbackNotes);
        feedbackActions(dialog, panel, () -> {
            String selected = (String) discomfort.getTag();
            if ("present".equals(selected) && emptyToNull(details.getText().toString()) == null) {
                details.setError("请填写部位和感受，或暂不填写反馈");
                return;
            }
            task.effort = emptyToNull(effort.getText().toString());
            task.discomfort = selected;
            task.discomfortNotes = "present".equals(selected) ? emptyToNull(details.getText().toString()) : null;
            task.feedbackNotes = emptyToNull(notes.getText().toString());
            touchTask(task);
            refresh.run();
            dialog.dismiss();
        });
        showFeedbackDialog(dialog, panel);
    }

    private void showSkipReason(WorkoutTask task, WorkoutExercise exercise, Runnable refresh) {
        Dialog dialog = new Dialog(this);
        LinearLayout panel = dialogPanel(exercise.name + " · 未做 / 少做原因", "只记录原因，不自动勾选或取消任何组。");
        EditText reason = feedbackInput(panel, "原因", exercise.skipReason);
        feedbackActions(dialog, panel, () -> {
            exercise.skipReason = emptyToNull(reason.getText().toString());
            touchTask(task);
            refresh.run();
            dialog.dismiss();
        }, "保存原因");
        showFeedbackDialog(dialog, panel);
    }

    private Button addExerciseButton(WorkoutTask task) {
        Button add = dialogButton("＋ 临时新增 / 补记动作", false);
        add.setOnClickListener(v -> {
            Dialog dialog = new Dialog(this);
            LinearLayout panel = dialogPanel("临时新增动作", "原计划保持不变；添加后填写各组实际重量、次数并勾选已完成组。");
            EditText name = feedbackInput(panel, "动作名称", null);
            EditText count = feedbackInput(panel, "组数", "4");
            count.setInputType(InputType.TYPE_CLASS_NUMBER);
            feedbackActions(dialog, panel, () -> {
                String exerciseName = emptyToNull(name.getText().toString());
                int number;
                try { number = Integer.parseInt(count.getText().toString()); }
                catch (NumberFormatException error) { number = 0; }
                if (exerciseName == null) { name.setError("请填写动作名称"); return; }
                if (number < 1 || number > 100) { count.setError("组数需为 1–100 的整数"); return; }
                WorkoutExercise exercise = new WorkoutExercise();
                exercise.id = UUID.randomUUID().toString().replace("-", "");
                exercise.name = exerciseName;
                exercise.isAdditional = true;
                for (int i = 1; i <= number; i++) {
                    WorkoutSet set = new WorkoutSet();
                    set.index = i;
                    exercise.sets.add(set);
                }
                task.exercises.add(exercise);
                touchTask(task);
                dialog.dismiss();
                openWorkoutTask(task);
                View page = findViewById(android.R.id.content);
                page.post(() -> {
                    ScrollView scroll = findWorkoutScroll(page);
                    if (scroll != null) scroll.fullScroll(View.FOCUS_DOWN);
                });
            }, "添加动作");
            showFeedbackDialog(dialog, panel);
        });
        LinearLayout.LayoutParams params = fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(48));
        params.setMargins(0, dp(16), 0, 0);
        add.setLayoutParams(params);
        return add;
    }

    private ScrollView findWorkoutScroll(View view) {
        if (view instanceof ScrollView) return (ScrollView) view;
        if (view instanceof android.view.ViewGroup) {
            android.view.ViewGroup group = (android.view.ViewGroup) view;
            for (int i = 0; i < group.getChildCount(); i++) {
                ScrollView result = findWorkoutScroll(group.getChildAt(i));
                if (result != null) return result;
            }
        }
        return null;
    }

    private void returnToTaskWorkspace() {
        cancelRestTimer();
        activeWorkoutTask = null;
        activeTab = TAB_TASK;
        setContentView(buildUi());
    }

    private void touchTask(WorkoutTask task) {
        for (WorkoutTask current : tasks) {
            if (current.id.equals(task.id) && current != task) {
                Toast.makeText(this, "任务已由同步更新，本次修改未保存，请重新进入任务", Toast.LENGTH_LONG).show();
                return;
            }
        }
        task.updatedAt = Math.max(System.currentTimeMillis(), task.updatedAt + 1);
        saveTasks();
    }

    private void startRestTimer(int seconds) {
        if (restTimer != null) restTimer.cancel();
        restSecondsRemaining = seconds;
        restTimer = new CountDownTimer(seconds * 1000L, 1000L) {
            @Override public void onTick(long millisUntilFinished) {
                restSecondsRemaining = Math.max(0, millisUntilFinished / 1000L);
                if (restTimerText != null) restTimerText.setText(formatTimer(restSecondsRemaining));
            }
            @Override public void onFinish() {
                restSecondsRemaining = 0;
                if (restTimerText != null) restTimerText.setText("00:00");
            }
        }.start();
    }

    private void cancelRestTimer() {
        if (restTimer != null) restTimer.cancel();
        restTimer = null;
        restSecondsRemaining = 0;
        if (restTimerText != null) restTimerText.setText("00:00");
    }

    private static boolean isTaskFinished(WorkoutTask task) {
        return "completed".equals(task.status) || "archived".equals(task.status);
    }

    private static boolean isTaskDeleted(WorkoutTask task) {
        return "deleted".equals(task.status);
    }

    private static boolean isTaskHidden(WorkoutTask task) {
        return isTaskDeleted(task) || "purged".equals(task.status);
    }

    private static int totalSets(WorkoutTask task) {
        int total = 0;
        for (WorkoutExercise exercise : task.exercises) total += exercise.sets.size();
        return total;
    }

    private static int completedSets(WorkoutTask task) {
        int total = 0;
        for (WorkoutExercise exercise : task.exercises)
            for (WorkoutSet set : exercise.sets) if (set.completed) total++;
        return total;
    }

    private static double workoutVolume(WorkoutTask task) {
        double total = 0;
        for (WorkoutExercise exercise : task.exercises)
            for (WorkoutSet set : exercise.sets) if (set.completed) total += set.actualWeight * set.actualReps;
        return total;
    }

    private static double parseDouble(String value, double fallback) {
        try { return Double.parseDouble(value.trim()); }
        catch (Exception ignored) { return fallback; }
    }

    private static String formatDecimal(double value) {
        return value == Math.rint(value) ? String.format(Locale.CHINA, "%.0f", value)
                : String.format(Locale.CHINA, "%.1f", value);
    }

    private static String formatTimer(long seconds) {
        return String.format(Locale.CHINA, "%02d:%02d", seconds / 60, seconds % 60);
    }

    private View buildRecycleBinPage() {
        LinearLayout page = pageLayout();
        page.setBackgroundColor(NOTE_BACKGROUND);
        LinearLayout toolbar = new LinearLayout(this);
        toolbar.setGravity(Gravity.CENTER_VERTICAL);
        Button back = button("‹");
        back.setTextSize(30);
        back.setBackground(rounded(Color.TRANSPARENT, 8));
        back.setContentDescription("返回工作区");
        back.setOnClickListener(v -> showTab(recycleBinReturnTab));
        toolbar.addView(back, fixed(dp(48), dp(48)));
        TextView title = text("回收站", 30, NOTE_PRIMARY);
        toolbar.addView(title, weighted());
        page.addView(toolbar, matchWrap());
        TextView subtitle = text("点击恢复，长按可永久删除", 13, NOTE_SECONDARY);
        subtitle.setPadding(0, dp(3), 0, dp(18));
        page.addView(subtitle);
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        LinearLayout cards = new LinearLayout(this);
        cards.setOrientation(LinearLayout.VERTICAL);
        List<WorkoutTask> deletedTasks = new ArrayList<>();
        for (WorkoutTask task : tasks) if (isTaskDeleted(task)) deletedTasks.add(task);
        deletedTasks.sort((left, right) -> Long.compare(right.deletedAt, left.deletedAt));
        if (recycleBin.isEmpty() && deletedTasks.isEmpty()) {
            TextView empty = text("回收站为空", 15, NOTE_SECONDARY);
            empty.setGravity(Gravity.CENTER);
            empty.setPadding(0, dp(96), 0, 0);
            cards.addView(empty, matchWrap());
        } else {
            if (!deletedTasks.isEmpty()) {
                TextView taskHeading = text("任务 · " + deletedTasks.size(), 14, NOTE_SECONDARY);
                taskHeading.setPadding(0, 0, 0, dp(10));
                cards.addView(taskHeading, matchWrap());
                for (WorkoutTask task : deletedTasks) cards.addView(recycledTaskCard(task));
            }
            if (!recycleBin.isEmpty()) {
                TextView noteHeading = text("文本 · " + recycleBin.size(), 14, NOTE_SECONDARY);
                noteHeading.setPadding(0, deletedTasks.isEmpty() ? 0 : dp(10), 0, dp(10));
                cards.addView(noteHeading, matchWrap());
            }
            for (Note note : recycleBin) cards.addView(noteCard(note, true));
        }
        scroll.addView(cards);
        page.addView(scroll, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));
        return page;
    }

    private View recycledTaskCard(WorkoutTask task) {
        LinearLayout card = new LinearLayout(this);
        card.setGravity(Gravity.CENTER_VERTICAL);
        card.setPadding(dp(16), dp(13), dp(14), dp(13));
        card.setBackground(rounded(SURFACE, 16));
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        TextView title = text(task.title, 16, NOTE_PRIMARY);
        title.setSingleLine(true);
        title.setEllipsize(TextUtils.TruncateAt.END);
        content.addView(title, matchWrap());
        String deletedAt = task.deletedAt > 0
                ? new SimpleDateFormat("yyyy年M月d日  HH:mm", Locale.CHINA).format(new Date(task.deletedAt))
                : "未知时间";
        content.addView(text("任务 · 删除于 " + deletedAt, 12, NOTE_SECONDARY), matchWrap());
        card.addView(content, weighted());
        Button restore = button("恢复");
        restore.setTextColor(PRIMARY);
        restore.setBackground(rounded(SURFACE_ACTIVE, 14));
        restore.setOnClickListener(v -> restoreTaskFromRecycleBin(task));
        card.addView(restore, fixed(dp(68), dp(42)));
        card.setOnLongClickListener(v -> {
            showPermanentDeleteTaskDialog(task);
            return true;
        });
        LinearLayout.LayoutParams params = matchWrap();
        params.setMargins(0, 0, 0, dp(10));
        card.setLayoutParams(params);
        return card;
    }

    private View noteCard(Note note, boolean inRecycleBin) {
        FrameLayout shell = new FrameLayout(this);
        LinearLayout.LayoutParams shellParams = matchWrap();
        shellParams.setMargins(0, 0, 0, dp(12));
        shell.setLayoutParams(shellParams);
        shell.setClipChildren(true);
        final Button[] swipeDelete = new Button[1];

        if (!inRecycleBin) {
            Button delete = button("删除");
            delete.setTextColor(Color.BLACK);
            delete.setBackground(rounded(Color.rgb(224, 87, 82), 18));
            delete.setAlpha(0f);
            delete.setEnabled(false);
            delete.setClickable(false);
            delete.setOnClickListener(v -> moveNoteToRecycleBin(note));
            swipeDelete[0] = delete;
            FrameLayout.LayoutParams deleteParams = new FrameLayout.LayoutParams(dp(88),
                    FrameLayout.LayoutParams.MATCH_PARENT, Gravity.END);
            shell.addView(delete, deleteParams);
        }

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.HORIZONTAL);
        card.setBackground(rounded(NOTE_CARD, 18));
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(dp(18), dp(16), dp(18), dp(16));
        TextView date = text(new SimpleDateFormat("M月d日", Locale.CHINA).format(new Date(note.updatedAt)), 13, PRIMARY);
        content.addView(date);
        TextView preview = text(preview(note.content), 17, NOTE_PRIMARY);
        preview.setMaxLines(3);
        preview.setPadding(0, dp(8), 0, dp(8));
        content.addView(preview);
        TextView timestamp = text(new SimpleDateFormat("yyyy年M月d日  HH:mm", Locale.CHINA).format(new Date(note.updatedAt)), 12, NOTE_SECONDARY);
        content.addView(timestamp);
        card.addView(content, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        if (inRecycleBin) {
            Button restore = button("恢复");
            restore.setTextColor(PRIMARY);
            restore.setBackground(rounded(SURFACE_ACTIVE, 14));
            restore.setOnClickListener(v -> restoreNoteFromRecycleBin(note));
            LinearLayout.LayoutParams restoreParams = fixed(dp(68), dp(42));
            restoreParams.setMargins(0, dp(18), dp(14), dp(18));
            card.addView(restore, restoreParams);
        }
        card.setOnClickListener(v -> {
            if (!inRecycleBin) v.post(() -> openNoteEditor(note));
        });
        if (inRecycleBin) {
            card.setOnLongClickListener(v -> {
                showDeleteNoteDialog(note, true);
                return true;
            });
        } else {
            final float[] startX = new float[1];
            final float[] startY = new float[1];
            final float[] startTranslation = new float[1];
            final boolean[] dragging = new boolean[1];
            final boolean[] verticalGesture = new boolean[1];
            final float revealWidth = dp(88);
            card.setOnTouchListener((view, event) -> {
                switch (event.getActionMasked()) {
                    case MotionEvent.ACTION_DOWN:
                        startX[0] = event.getRawX();
                        startY[0] = event.getRawY();
                        startTranslation[0] = view.getTranslationX();
                        dragging[0] = false;
                        verticalGesture[0] = false;
                        return true;
                    case MotionEvent.ACTION_MOVE:
                        float deltaX = event.getRawX() - startX[0];
                        float deltaY = event.getRawY() - startY[0];
                        if (!dragging[0] && !verticalGesture[0]) {
                            if (Math.abs(deltaX) < dp(8) && Math.abs(deltaY) < dp(8)) return true;
                            if (Math.abs(deltaX) <= Math.abs(deltaY)) {
                                verticalGesture[0] = true;
                                view.getParent().requestDisallowInterceptTouchEvent(false);
                                return false;
                            }
                            dragging[0] = true;
                            view.getParent().requestDisallowInterceptTouchEvent(true);
                        }
                        if (dragging[0]) {
                            view.setTranslationX(Math.max(-revealWidth, Math.min(0, startTranslation[0] + deltaX)));
                            swipeDelete[0].setAlpha(Math.min(1f, -view.getTranslationX() / revealWidth));
                        }
                        return true;
                    case MotionEvent.ACTION_UP:
                        view.getParent().requestDisallowInterceptTouchEvent(false);
                        if (dragging[0]) {
                            boolean revealDelete = view.getTranslationX() < -revealWidth / 2f;
                            view.animate().translationX(revealDelete ? -revealWidth : 0).setDuration(160).start();
                            swipeDelete[0].animate().alpha(revealDelete ? 1f : 0f).setDuration(160).start();
                            swipeDelete[0].setEnabled(revealDelete);
                            swipeDelete[0].setClickable(revealDelete);
                            return true;
                        }
                        float totalX = event.getRawX() - startX[0];
                        float totalY = event.getRawY() - startY[0];
                        if (!verticalGesture[0] && Math.abs(totalX) < dp(8) && Math.abs(totalY) < dp(8)) {
                            view.performClick();
                        }
                        return true;
                    case MotionEvent.ACTION_CANCEL:
                        view.getParent().requestDisallowInterceptTouchEvent(false);
                        if (dragging[0]) {
                            view.animate().translationX(0).setDuration(160).start();
                            swipeDelete[0].animate().alpha(0f).setDuration(160).start();
                            swipeDelete[0].setEnabled(false);
                            swipeDelete[0].setClickable(false);
                        }
                        return true;
                    default:
                        return false;
                }
            });
        }
        shell.addView(card, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT));
        return shell;
    }

    private void moveNoteToRecycleBin(Note note) {
        notes.remove(note);
        recycleBin.add(0, note);
        saveNotes();
        showTab(TAB_TEXT);
    }

    private void restoreNoteFromRecycleBin(Note note) {
        recycleBin.remove(note);
        long restoredAt = System.currentTimeMillis();
        Note restored = note;
        for (Note active : notes) {
            if (active.id.equals(note.id)) {
                restored = new Note(UUID.randomUUID().toString(), note.title, note.content,
                        note.fontSize, note.lineSpacing, note.letterSpacing, restoredAt);
                break;
            }
        }
        restored.updatedAt = restoredAt;
        notes.add(0, restored);
        saveNotes();
        showTab(TAB_RECYCLE_BIN);
    }

    private void openNoteEditor(Note note) {
        editingNote = note;
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setBackgroundColor(NOTE_EDITOR_BACKGROUND);
        page.setFitsSystemWindows(true);
        page.setPadding(dp(12), dp(8), dp(12), 0);
        LinearLayout toolbar = new LinearLayout(this);
        toolbar.setGravity(Gravity.CENTER_VERTICAL);
        Button back = button("‹");
        back.setTextSize(32);
        back.setTextColor(PRIMARY);
        back.setBackground(rounded(Color.TRANSPARENT, 8));
        back.setContentDescription("返回文本列表");
        back.setOnClickListener(v -> finishEditing());
        toolbar.addView(back, fixed(dp(48), dp(48)));
        TextView title = text(note.title, 18, NOTE_EDITOR_TEXT);
        title.setGravity(Gravity.CENTER);
        title.setSingleLine(true);
        title.setEllipsize(TextUtils.TruncateAt.END);
        title.setContentDescription("修改文本标题");
        title.setOnClickListener(v -> showTitleEditor(title));
        toolbar.addView(title, weighted());
        Button more = button("···");
        more.setTextSize(22);
        more.setTextColor(NOTE_EDITOR_TEXT);
        more.setBackground(rounded(Color.TRANSPARENT, 8));
        more.setContentDescription("文本操作");
        more.setOnClickListener(v -> showNoteEditorMenu(v));
        toolbar.addView(more, fixed(dp(48), dp(48)));
        page.addView(toolbar, matchWrap());

        noteEditor = new ChecklistEditText(this);
        configureInputColors(noteEditor);
        noteEditor.setInitialText(note.content);
        noteEditor.setHint("开始记录……");
        noteEditor.setHintTextColor(NOTE_EDITOR_SECONDARY);
        noteEditor.setTextColor(NOTE_EDITOR_TEXT);
        noteEditor.setTextSize(note.fontSize);
        noteEditor.setLineSpacing(0, note.lineSpacing);
        noteEditor.setLetterSpacing(note.letterSpacing);
        noteEditor.setGravity(Gravity.TOP | Gravity.START);
        noteEditor.setPadding(dp(8), dp(16), dp(8), dp(16));
        noteEditor.setBackgroundColor(Color.TRANSPARENT);
        noteEditor.setSingleLine(false);
        noteEditor.setTag(note);
        ScrollView editorScroll = new ScrollView(this);
        editorScroll.setFillViewport(true);
        editorScroll.addView(noteEditor, new ScrollView.LayoutParams(
                ScrollView.LayoutParams.MATCH_PARENT, ScrollView.LayoutParams.WRAP_CONTENT));
        page.addView(editorScroll, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));
        LinearLayout.LayoutParams functionBarParams = matchWrap();
        functionBarParams.setMargins(0, 0, 0, dp(8));
        page.addView(buildNoteEditorFunctionBar(), functionBarParams);
        noteEditor.setHistoryChangedListener(this::updateEditorHistoryButtons);
        updateEditorHistoryButtons();
        setContentView(page);
        noteEditor.requestFocus();
        noteEditor.setSelection(noteEditor.length());
    }

    private View buildNoteEditorFunctionBar() {
        LinearLayout bar = new LinearLayout(this);
        bar.setGravity(Gravity.CENTER_VERTICAL);
        bar.setPadding(dp(6), dp(4), dp(6), dp(4));
        bar.setBackground(rounded(SURFACE, 17));
        bar.setElevation(dp("Silver".equals(visualTheme) ? 2 : 6));

        undoButton = editorToolButton("↶", 28, "撤销");
        undoButton.setOnClickListener(v -> noteEditor.performUndo());
        bar.addView(undoButton, fixed(dp(46), dp(46)));

        redoButton = editorToolButton("↷", 28, "重做");
        redoButton.setOnClickListener(v -> noteEditor.performRedo());
        bar.addView(redoButton, fixed(dp(46), dp(46)));

        TextView todo = editorToolButton("☑", 21, "添加或移除待办框");
        todo.setOnClickListener(v -> noteEditor.toggleTodoForCurrentLine());
        bar.addView(todo, fixed(dp(46), dp(46)));

        TextView fontSmaller = editorToolButton("−", 24, "减小字号");
        fontSmaller.setOnClickListener(v -> applyNoteFontSize(editingNote.fontSize - 1));
        bar.addView(fontSmaller, fixed(dp(38), dp(46)));

        fontSizeValue = editorToolButton(String.valueOf(editingNote.fontSize), 14, "当前字号");
        fontSizeValue.setClickable(false);
        fontSizeValue.setFocusable(false);
        bar.addView(fontSizeValue, fixed(dp(42), dp(46)));

        TextView fontLarger = editorToolButton("+", 22, "增大字号");
        fontLarger.setOnClickListener(v -> applyNoteFontSize(editingNote.fontSize + 1));
        bar.addView(fontLarger, fixed(dp(38), dp(46)));

        TextView spacing = editorToolButton("间距", 13, "调整行距和字距");
        spacing.setOnClickListener(this::showTextSpacingMenu);
        bar.addView(spacing, fixed(dp(62), dp(46)));
        return bar;
    }

    private TextView editorToolButton(String label, int sp, String description) {
        TextView button = text(label, sp, TEXT_PRIMARY);
        button.setGravity(Gravity.CENTER);
        button.setContentDescription(description);
        button.setBackground(rounded(Color.TRANSPARENT, 12));
        button.setClickable(true);
        button.setFocusable(true);
        return button;
    }

    private void updateEditorHistoryButtons() {
        if (noteEditor == null || undoButton == null || redoButton == null) return;
        undoButton.setEnabled(noteEditor.canUndo());
        redoButton.setEnabled(noteEditor.canRedo());
        undoButton.setAlpha(undoButton.isEnabled() ? 0.88f : 0.25f);
        redoButton.setAlpha(redoButton.isEnabled() ? 0.88f : 0.25f);
    }

    private void showTextSpacingMenu(View anchor) {
        if (noteEditor == null || editingNote == null) return;
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(8), dp(8), dp(8), dp(8));
        panel.setBackground(rounded(SURFACE, 16));
        TextView heading = text("排版间距", 13, TEXT_SECONDARY);
        heading.setPadding(dp(10), dp(4), dp(10), dp(6));
        panel.addView(heading, matchWrap());

        TextView lineValue = text(formatDecimal(editingNote.lineSpacing), 14, TEXT_PRIMARY);
        panel.addView(spacingControlRow("行距", lineValue,
                () -> applyNoteLineSpacing(editingNote.lineSpacing - 0.1f),
                () -> applyNoteLineSpacing(editingNote.lineSpacing + 0.1f)), matchWrap());

        TextView letterValue = text(formatDecimal(editingNote.letterSpacing), 14, TEXT_PRIMARY);
        LinearLayout letterRow = spacingControlRow("字距", letterValue,
                () -> applyNoteLetterSpacing(editingNote.letterSpacing - 0.02f),
                () -> applyNoteLetterSpacing(editingNote.letterSpacing + 0.02f));
        LinearLayout.LayoutParams letterParams = matchWrap();
        letterParams.setMargins(0, dp(4), 0, 0);
        panel.addView(letterRow, letterParams);

        TextView reset = editorToolButton("恢复舒适默认值", 13, "恢复默认排版间距");
        reset.setTextColor(PRIMARY);
        reset.setBackground(rounded(SURFACE_ACTIVE, 11));
        reset.setOnClickListener(v -> {
            applyNoteLineSpacing(1.3f);
            applyNoteLetterSpacing(0.02f);
            lineValue.setText(formatDecimal(editingNote.lineSpacing));
            letterValue.setText(formatDecimal(editingNote.letterSpacing));
        });
        LinearLayout.LayoutParams resetParams = matchWrap();
        resetParams.height = dp(42);
        resetParams.setMargins(0, dp(6), 0, 0);
        panel.addView(reset, resetParams);

        PopupWindow popup = new PopupWindow(panel, dp(236), LinearLayout.LayoutParams.WRAP_CONTENT, false);
        popup.setBackgroundDrawable(new ColorDrawable(Color.TRANSPARENT));
        popup.setOutsideTouchable(true);
        popup.setElevation(dp(12));
        popup.setInputMethodMode(PopupWindow.INPUT_METHOD_NOT_NEEDED);
        popup.showAsDropDown(anchor, -dp(174), -dp(238));
    }

    private LinearLayout spacingControlRow(String label, TextView value, Runnable decrease, Runnable increase) {
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(dp(10), 0, dp(6), 0);
        row.setBackground(rounded(SURFACE_ACTIVE, 11));
        row.addView(text(label, 14, TEXT_PRIMARY), weighted());
        TextView minus = editorToolButton("−", 22, "减小" + label);
        minus.setOnClickListener(v -> {
            decrease.run();
            value.setText("行距".equals(label) ? formatDecimal(editingNote.lineSpacing)
                    : formatDecimal(editingNote.letterSpacing));
        });
        row.addView(minus, fixed(dp(38), dp(46)));
        value.setGravity(Gravity.CENTER);
        row.addView(value, fixed(dp(50), dp(46)));
        TextView plus = editorToolButton("+", 20, "增大" + label);
        plus.setOnClickListener(v -> {
            increase.run();
            value.setText("行距".equals(label) ? formatDecimal(editingNote.lineSpacing)
                    : formatDecimal(editingNote.letterSpacing));
        });
        row.addView(plus, fixed(dp(38), dp(46)));
        return row;
    }

    private void applyNoteFontSize(int size) {
        size = normalizeFontSize(size);
        if (noteEditor == null || editingNote == null || editingNote.fontSize == size) return;
        persistEditingNote();
        editingNote.fontSize = size;
        noteEditor.setTextSize(size);
        if (fontSizeValue != null) fontSizeValue.setText(String.valueOf(size));
        saveEditingNoteTypography();
    }

    private void applyNoteLineSpacing(float value) {
        value = normalizeLineSpacing(value);
        if (noteEditor == null || editingNote == null || editingNote.lineSpacing == value) return;
        persistEditingNote();
        editingNote.lineSpacing = value;
        noteEditor.setLineSpacing(0, value);
        saveEditingNoteTypography();
    }

    private void applyNoteLetterSpacing(float value) {
        value = normalizeLetterSpacing(value);
        if (noteEditor == null || editingNote == null || editingNote.letterSpacing == value) return;
        persistEditingNote();
        editingNote.letterSpacing = value;
        noteEditor.setLetterSpacing(value);
        saveEditingNoteTypography();
    }

    private void saveEditingNoteTypography() {
        editingNote.updatedAt = System.currentTimeMillis();
        if (!notes.contains(editingNote)) return;
        notes.remove(editingNote);
        notes.add(0, editingNote);
        saveNotes();
    }

    private void showNoteEditorMenu(View anchor) {
        if (noteEditor == null) return;
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(8), dp(8), dp(8), dp(8));
        panel.setBackground(rounded(SURFACE, 16));
        TextView todoAction = text(noteEditor.isCurrentLineTodo() ? "移除待办框" : "添加待办框", 16, TEXT_PRIMARY);
        todoAction.setGravity(Gravity.CENTER_VERTICAL);
        todoAction.setPadding(dp(16), 0, dp(16), 0);
        todoAction.setBackground(rounded(SURFACE_ACTIVE, 12));
        panel.addView(todoAction, new LinearLayout.LayoutParams(dp(190), dp(50)));
        PopupWindow popup = new PopupWindow(panel, dp(206), LinearLayout.LayoutParams.WRAP_CONTENT, true);
        popup.setBackgroundDrawable(new ColorDrawable(Color.TRANSPARENT));
        popup.setOutsideTouchable(true);
        popup.setElevation(dp(12));
        todoAction.setOnClickListener(v -> {
            noteEditor.toggleTodoForCurrentLine();
            popup.dismiss();
        });
        popup.showAsDropDown(anchor, -dp(158), dp(2));
    }

    private void showTitleEditor(TextView titleView) {
        if (editingNote == null) return;
        Dialog dialog = new Dialog(this);
        LinearLayout panel = dialogPanel("修改标题", "标题会随文本记录一起保存和同步");
        EditText input = new EditText(this);
        configureInputColors(input);
        input.setSingleLine(true);
        input.setText(editingNote.title);
        input.setTextColor(TEXT_PRIMARY);
        input.setHintTextColor(TEXT_SECONDARY);
        input.setTextSize(17);
        input.setPadding(dp(14), 0, dp(14), 0);
        input.setBackground(rounded(SURFACE_ACTIVE, 12));
        panel.addView(input, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(52)));

        LinearLayout actions = new LinearLayout(this);
        actions.setGravity(Gravity.END);
        Button cancel = dialogButton("取消", false);
        cancel.setOnClickListener(v -> dialog.dismiss());
        Button save = dialogButton("保存", false);
        save.setTextColor(BACKGROUND);
        save.setBackground(rounded(PRIMARY, 12));
        save.setOnClickListener(v -> {
            String value = input.getText().toString().trim();
            if (value.isEmpty()) {
                input.setHint("请输入标题");
                return;
            }
            persistEditingNote();
            editingNote.title = value;
            editingNote.updatedAt = System.currentTimeMillis();
            titleView.setText(value);
            if (notes.contains(editingNote)) {
                notes.remove(editingNote);
                notes.add(0, editingNote);
                saveNotes();
            }
            dialog.dismiss();
        });
        LinearLayout.LayoutParams actionParams = fixed(dp(78), dp(44));
        actionParams.setMargins(dp(8), dp(16), 0, 0);
        actions.addView(cancel, actionParams);
        LinearLayout.LayoutParams saveParams = fixed(dp(78), dp(44));
        saveParams.setMargins(dp(8), dp(16), 0, 0);
        actions.addView(save, saveParams);
        panel.addView(actions, matchWrap());
        showThemedDialog(dialog, panel);
        input.requestFocus();
        input.selectAll();
    }

    private void finishEditing() {
        saveEditingNote();
        activeTab = TAB_TEXT;
        setContentView(buildUi());
    }

    private void saveEditingNote() {
        persistEditingNote();
        editingNote = null;
        noteEditor = null;
    }

    private void persistEditingNote() {
        if (editingNote == null) return;
        String value = noteEditor == null ? editingNote.content : noteEditor.getText().toString().trim();
        if (!value.isEmpty() && !value.equals(editingNote.content)) {
            editingNote.content = value;
            editingNote.updatedAt = System.currentTimeMillis();
            notes.remove(editingNote);
            notes.add(0, editingNote);
            saveNotes();
        }
    }

    private void persistEditingNoteOnUiThread() throws Exception {
        if (Looper.myLooper() == Looper.getMainLooper()) {
            persistEditingNote();
            return;
        }
        FutureTask<Void> task = new FutureTask<>(() -> {
            persistEditingNote();
            return null;
        });
        runOnUiThread(task);
        task.get();
    }

    @Override
    public void onBackPressed() {
        if (activeWorkoutTask != null) {
            returnToTaskWorkspace();
            return;
        }
        if (editingNote != null) {
            finishEditing();
            return;
        }
        if (activeTab == TAB_RECYCLE_BIN) {
            showTab(recycleBinReturnTab);
            return;
        }
        super.onBackPressed();
    }

    private void showDeleteNoteDialog(Note note, boolean permanent) {
        String title = permanent ? "永久删除这条记录？" : "删除这条记录？";
        String action = permanent ? "永久删除" : "删除";
        Dialog dialog = new Dialog(this);
        LinearLayout panel = dialogPanel(title,
                permanent ? "永久删除后无法恢复。" : "删除后仍可从回收站恢复。");
        LinearLayout actions = new LinearLayout(this);
        actions.setGravity(Gravity.END);
        Button cancel = dialogButton("取消", false);
        cancel.setOnClickListener(v -> dialog.dismiss());
        Button confirm = dialogButton(action, true);
        confirm.setOnClickListener(v -> {
            if (permanent) {
                recycleBin.remove(note);
            } else {
                notes.remove(note);
                recycleBin.add(0, note);
            }
            saveNotes();
            dialog.dismiss();
            showTab(permanent ? TAB_RECYCLE_BIN : TAB_TEXT);
        });
        LinearLayout.LayoutParams cancelParams = fixed(dp(78), dp(44));
        cancelParams.setMargins(dp(8), dp(16), 0, 0);
        actions.addView(cancel, cancelParams);
        LinearLayout.LayoutParams confirmParams = fixed(dp(96), dp(44));
        confirmParams.setMargins(dp(8), dp(16), 0, 0);
        actions.addView(confirm, confirmParams);
        panel.addView(actions, matchWrap());
        showThemedDialog(dialog, panel);
    }

    private void showDeleteTaskDialog(WorkoutTask task) {
        Dialog dialog = new Dialog(this);
        LinearLayout panel = dialogPanel("将任务移入回收站？", "任务可在回收站恢复，并会在下次同步时更新到电脑。\n" + task.title);
        LinearLayout actions = new LinearLayout(this);
        actions.setGravity(Gravity.END);
        Button cancel = dialogButton("取消", false);
        cancel.setOnClickListener(v -> dialog.dismiss());
        Button confirm = dialogButton("删除", true);
        confirm.setOnClickListener(v -> {
            task.previousStatus = task.status;
            task.status = "deleted";
            task.deletedAt = System.currentTimeMillis();
            task.updatedAt = task.deletedAt;
            saveTasks();
            dialog.dismiss();
            showTab(TAB_TASK);
        });
        LinearLayout.LayoutParams cancelParams = fixed(dp(78), dp(44));
        cancelParams.setMargins(dp(8), dp(16), 0, 0);
        actions.addView(cancel, cancelParams);
        LinearLayout.LayoutParams confirmParams = fixed(dp(96), dp(44));
        confirmParams.setMargins(dp(8), dp(16), 0, 0);
        actions.addView(confirm, confirmParams);
        panel.addView(actions, matchWrap());
        showThemedDialog(dialog, panel);
    }

    private void restoreTaskFromRecycleBin(WorkoutTask task) {
        String restoredStatus = task.previousStatus;
        if (!("pending".equals(restoredStatus) || "active".equals(restoredStatus)
                || "completed".equals(restoredStatus) || "archived".equals(restoredStatus))) {
            restoredStatus = task.completedAt > 0 ? "completed" : completedSets(task) > 0 ? "active" : "pending";
        }
        task.status = restoredStatus;
        task.previousStatus = "";
        task.deletedAt = 0;
        task.updatedAt = System.currentTimeMillis();
        saveTasks();
        showTab(TAB_RECYCLE_BIN);
    }

    private void showPermanentDeleteTaskDialog(WorkoutTask task) {
        if (permanentTaskDeleteDialogVisible || !isTaskDeleted(task)) return;
        permanentTaskDeleteDialogVisible = true;
        Dialog dialog = new Dialog(this);
        LinearLayout panel = dialogPanel("永久删除这个任务？", "永久删除后无法恢复。\n" + task.title);
        LinearLayout actions = new LinearLayout(this);
        actions.setGravity(Gravity.END);
        Button cancel = dialogButton("取消", false);
        cancel.setOnClickListener(v -> dialog.dismiss());
        Button confirm = dialogButton("永久删除", true);
        confirm.setEnabled(false);
        confirm.setAlpha(0.45f);
        confirm.setOnClickListener(v -> {
            long purgedAt = System.currentTimeMillis();
            if (!isTaskDeleted(task) || purgedAt - lastTaskPurgedAt < TASK_PURGE_COOLDOWN_MS) return;
            lastTaskPurgedAt = purgedAt;
            confirm.setEnabled(false);
            task.title = "已永久删除任务";
            task.sourceProject = "";
            task.status = "purged";
            task.previousStatus = "";
            task.scheduledDate = "";
            task.estimatedMinutes = 0;
            task.notes = "";
            task.effort = task.discomfort = task.discomfortNotes = task.feedbackNotes = null;
            task.createdAt = 0;
            task.startedAt = 0;
            task.completedAt = 0;
            task.deletedAt = purgedAt;
            task.updatedAt = purgedAt;
            task.exercises.clear();
            saveTasks();
            dialog.dismiss();
            contentRoot.postDelayed(() -> showTab(TAB_RECYCLE_BIN), 120);
        });
        LinearLayout.LayoutParams cancelParams = fixed(dp(78), dp(44));
        cancelParams.setMargins(dp(8), dp(16), 0, 0);
        actions.addView(cancel, cancelParams);
        LinearLayout.LayoutParams confirmParams = fixed(dp(112), dp(44));
        confirmParams.setMargins(dp(8), dp(16), 0, 0);
        actions.addView(confirm, confirmParams);
        panel.addView(actions, matchWrap());
        dialog.setOnDismissListener(ignored -> permanentTaskDeleteDialogVisible = false);
        showThemedDialog(dialog, panel);
        confirm.postDelayed(() -> {
            if (!dialog.isShowing()) return;
            confirm.setEnabled(true);
            confirm.setAlpha(1f);
        }, TASK_PURGE_ARM_DELAY_MS);
    }

    private View buildTransferPage() {
        LinearLayout page = pageLayout();
        LinearLayout transferHeader = new LinearLayout(this);
        transferHeader.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout heading = new LinearLayout(this);
        heading.setOrientation(LinearLayout.VERTICAL);
        heading.addView(text("传输", 30, TEXT_PRIMARY));
        TextView transferCaption = text("局域网内的轻量投送", 13, TEXT_SECONDARY);
        transferCaption.setPadding(0, dp(3), 0, 0);
        heading.addView(transferCaption);
        transferHeader.addView(heading, weighted());
        page.addView(transferHeader, matchWrap());

        LinearLayout connectionCard = new LinearLayout(this);
        connectionCard.setOrientation(LinearLayout.VERTICAL);
        connectionCard.setBackground(rounded(SURFACE, 20));
        connectionCard.setPadding(dp(18), dp(16), dp(18), dp(16));
        LinearLayout statusRow = new LinearLayout(this);
        statusRow.setGravity(Gravity.CENTER_VERTICAL);
        View dot = new View(this);
        dot.setBackground(rounded(connected ? connectionColor() : PRIMARY, 5));
        statusRow.addView(dot, fixed(dp(10), dp(10)));
        statusText = text(connectionStatus, 16, connected ? connectionColor() : TEXT_PRIMARY);
        statusText.setPadding(dp(10), 0, 0, 0);
        statusRow.addView(statusText, weighted());
        connectionCard.addView(statusRow, matchWrap());
        peerText = text(connectionDetail, 13, TEXT_SECONDARY);
        peerText.setPadding(dp(20), dp(8), 0, 0);
        connectionCard.addView(peerText, matchWrap());
        LinearLayout.LayoutParams connectionParams = matchWrap();
        connectionParams.setMargins(0, dp(20), 0, dp(16));
        page.addView(connectionCard, connectionParams);

        messageEdit = new EditText(this);
        configureInputColors(messageEdit);
        messageEdit.setHint("发送一段文字到已连接的电脑");
        messageEdit.setHintTextColor(TEXT_SECONDARY);
        messageEdit.setTextColor(TEXT_PRIMARY);
        messageEdit.setTextSize(15);
        messageEdit.setSingleLine(true);
        messageEdit.setBackground(rounded(SURFACE, 18));
        messageEdit.setPadding(dp(18), 0, dp(18), 0);
        page.addView(messageEdit, fixed(LinearLayout.LayoutParams.MATCH_PARENT, dp(54)));

        LinearLayout actions = new LinearLayout(this);
        actions.setOrientation(LinearLayout.HORIZONTAL);
        actions.setPadding(0, dp(10), 0, dp(20));
        sendTextButton = button("发送");
        sendTextButton.setTextColor(BACKGROUND);
        sendTextButton.setBackground(rounded(PRIMARY, 16));
        sendFileButton = button("选择文件");
        sendFileButton.setBackground(rounded(SURFACE_ACTIVE, 16));
        disconnectButton = button("断开");
        disconnectButton.setTextColor(TEXT_SECONDARY);
        disconnectButton.setBackground(rounded(Color.TRANSPARENT, 16));
        sendTextButton.setEnabled(connected);
        sendFileButton.setEnabled(connected);
        disconnectButton.setEnabled(connected);
        sendTextButton.setOnClickListener(v -> sendText());
        sendFileButton.setOnClickListener(v -> chooseFile());
        disconnectButton.setOnClickListener(v -> {
            suppressAutoReconnect = true;
            disconnect("已手动断开");
        });
        actions.addView(sendTextButton, weighted(dp(48)));
        actions.addView(sendFileButton, weighted(dp(48)));
        actions.addView(disconnectButton, weighted(dp(48)));
        page.addView(actions);

        ScrollView scroll = new ScrollView(this);
        scroll.setBackground(rounded(SURFACE, 20));
        logText = text(transferLog.toString(), 13, TEXT_SECONDARY);
        logText.setPadding(dp(18), dp(18), dp(18), dp(18));
        scroll.addView(logText);
        LinearLayout.LayoutParams scrollParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f);
        page.addView(scroll, scrollParams);

        TextView footer = text("接收文件保存到  下载 / 局域投送", 12, TEXT_SECONDARY);
        footer.setGravity(Gravity.CENTER);
        footer.setPadding(0, dp(12), 0, 0);
        page.addView(footer);
        return page;
    }

    private LinearLayout pageLayout() {
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(20), dp(12), dp(20), dp(8));
        return page;
    }

    private Button navigationButton(String label, int tab) {
        Button button = button(label);
        button.setTextSize(13);
        button.setOnClickListener(v -> showTab(tab));
        return button;
    }

    private void updateNavigation() {
        styleNavigationButton(textTabButton, activeTab == TAB_TEXT);
        styleNavigationButton(taskTabButton, activeTab == TAB_TASK);
        styleNavigationButton(transferTabButton, activeTab == TAB_TRANSFER);
    }

    private void styleNavigationButton(Button button, boolean selected) {
        button.setTextColor(selected ? TEXT_PRIMARY : TEXT_SECONDARY);
        button.setBackground(rounded(selected ? SURFACE_ACTIVE : Color.TRANSPARENT, 15));
    }

    private GradientDrawable rounded(int color, int radiusDp) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(color);
        drawable.setCornerRadius(dp("Silver".equals(visualTheme) ? Math.min(radiusDp, 14) : radiusDp));
        if ("Silver".equals(visualTheme) && color == SURFACE)
            drawable.setStroke(dp(1), Color.rgb(222, 225, 231));
        return drawable;
    }

    private int connectionColor() {
        return "Silver".equals(visualTheme) ? PRIMARY : Color.rgb(99, 222, 151);
    }

    private void configureInputColors(EditText input) {
        input.setHighlightColor("Silver".equals(visualTheme) ? Color.argb(55, 84, 89, 103) : Color.argb(70, 190, 165, 255));
        if (Build.VERSION.SDK_INT >= 29) {
            GradientDrawable cursor = new GradientDrawable();
            cursor.setColor(TEXT_PRIMARY);
            cursor.setSize(dp(2), dp(24));
            input.setTextCursorDrawable(cursor);
            for (android.graphics.drawable.Drawable handle : new android.graphics.drawable.Drawable[] {
                    input.getTextSelectHandle(), input.getTextSelectHandleLeft(), input.getTextSelectHandleRight() }) {
                if (handle != null) handle.mutate().setTint(PRIMARY);
            }
        }
    }

    private void loadNotes() {
        String savedNotes = getPreferences(MODE_PRIVATE).getString(NOTES_KEY, null);
        readNotes(savedNotes, notes);
        readNotes(getPreferences(MODE_PRIVATE).getString(RECYCLE_BIN_KEY, null), recycleBin);
        if (savedNotes == null) {
            String legacyNote = getPreferences(MODE_PRIVATE).getString("note", "").trim();
            if (!legacyNote.isEmpty()) {
                notes.add(new Note(UUID.randomUUID().toString(), defaultNoteTitle(legacyNote),
                        legacyNote, 18, System.currentTimeMillis()));
            }
            saveNotes();
        }
    }

    private void readNotes(String raw, List<Note> target) {
        if (raw == null || raw.isEmpty()) return;
        try {
            JSONArray array = new JSONArray(raw);
            for (int i = 0; i < array.length(); i++) {
                JSONObject item = array.getJSONObject(i);
                String id = item.optString("id");
                String content = item.optString("content");
                if (!id.isEmpty() && !content.trim().isEmpty()) {
                    target.add(new Note(id, item.optString("title", defaultNoteTitle(content)), content,
                            normalizeFontSize(item.optInt("fontSize", 18)),
                            normalizeLineSpacing(item.optDouble("lineSpacing", 1.3)),
                            normalizeLetterSpacing(item.optDouble("letterSpacing", 0.02)),
                            item.optLong("updatedAt", System.currentTimeMillis())));
                }
            }
        } catch (Exception ignored) {
        }
    }

    private void saveNotes() {
        getPreferences(MODE_PRIVATE).edit()
                .putString(NOTES_KEY, notesToJson(notes).toString())
                .putString(RECYCLE_BIN_KEY, notesToJson(recycleBin).toString())
                .apply();
    }

    private JSONArray notesToJson(List<Note> source) {
        JSONArray array = new JSONArray();
        for (Note note : source) {
            JSONObject item = new JSONObject();
            try {
                item.put("id", note.id);
                item.put("title", note.title);
                item.put("content", note.content);
                item.put("fontSize", note.fontSize);
                item.put("lineSpacing", note.lineSpacing);
                item.put("letterSpacing", note.letterSpacing);
                item.put("updatedAt", note.updatedAt);
                array.put(item);
            } catch (Exception ignored) {
            }
        }
        return array;
    }

    private void loadTasks() {
        tasks.clear();
        String raw = getPreferences(MODE_PRIVATE).getString(TASKS_KEY, null);
        if (raw == null || raw.isEmpty()) return;
        try {
            JSONArray array = new JSONArray(raw);
            for (int i = 0; i < array.length(); i++) {
                WorkoutTask task = taskFromJson(array.getJSONObject(i));
                if (task != null) tasks.add(task);
            }
        } catch (Exception ignored) {
        }
    }

    private void saveTasks() {
        getPreferences(MODE_PRIVATE).edit().putString(TASKS_KEY, tasksToJson().toString()).apply();
    }

    private JSONArray tasksToJson() {
        JSONArray array = new JSONArray();
        for (WorkoutTask task : tasks) array.put(taskToJson(task));
        return array;
    }

    private JSONObject taskToJson(WorkoutTask task) {
        JSONObject item = new JSONObject();
        try {
            item.put("id", task.id);
            item.put("title", task.title);
            item.put("sourceProject", task.sourceProject);
            item.put("status", task.status);
            item.put("previousStatus", task.previousStatus);
            item.put("scheduledDate", task.scheduledDate);
            item.put("estimatedMinutes", task.estimatedMinutes);
            item.put("notes", task.notes);
            item.put("effort", task.effort == null ? JSONObject.NULL : task.effort);
            item.put("discomfort", task.discomfort == null ? JSONObject.NULL : task.discomfort);
            item.put("discomfortNotes", task.discomfortNotes == null ? JSONObject.NULL : task.discomfortNotes);
            item.put("feedbackNotes", task.feedbackNotes == null ? JSONObject.NULL : task.feedbackNotes);
            item.put("createdAt", task.createdAt);
            item.put("updatedAt", task.updatedAt);
            item.put("startedAt", task.startedAt);
            item.put("completedAt", task.completedAt);
            item.put("deletedAt", task.deletedAt);
            JSONArray exercises = new JSONArray();
            for (WorkoutExercise exercise : task.exercises) {
                JSONObject exerciseJson = new JSONObject();
                exerciseJson.put("id", exercise.id);
                exerciseJson.put("name", exercise.name);
                exerciseJson.put("restSeconds", exercise.restSeconds);
                exerciseJson.put("notes", exercise.notes);
                exerciseJson.put("isAdditional", exercise.isAdditional);
                exerciseJson.put("skipReason", exercise.skipReason == null ? JSONObject.NULL : exercise.skipReason);
                JSONArray sets = new JSONArray();
                for (WorkoutSet set : exercise.sets) {
                    JSONObject setJson = new JSONObject();
                    setJson.put("index", set.index);
                    setJson.put("targetWeight", set.targetWeight);
                    setJson.put("targetReps", set.targetReps);
                    setJson.put("actualWeight", set.actualWeight);
                    setJson.put("actualReps", set.actualReps);
                    setJson.put("completed", set.completed);
                    setJson.put("completedAt", set.completedAt);
                    setJson.put("rir", set.rir == null ? JSONObject.NULL : set.rir);
                    setJson.put("movementQuality", set.movementQuality == null ? JSONObject.NULL : set.movementQuality);
                    setJson.put("discomfort", set.discomfort == null ? JSONObject.NULL : set.discomfort);
                    setJson.put("discomfortNotes", set.discomfortNotes == null ? JSONObject.NULL : set.discomfortNotes);
                    sets.put(setJson);
                }
                exerciseJson.put("sets", sets);
                exercises.put(exerciseJson);
            }
            item.put("exercises", exercises);
        } catch (Exception ignored) {
        }
        return item;
    }

    private WorkoutTask taskFromJson(JSONObject item) {
        String id = item.optString("id");
        if (id.isEmpty()) return null;
        WorkoutTask task = new WorkoutTask();
        task.id = id;
        task.title = item.optString("title", "未命名训练");
        task.sourceProject = item.optString("sourceProject", "D");
        task.status = item.optString("status", "pending");
        task.previousStatus = item.optString("previousStatus");
        task.scheduledDate = item.optString("scheduledDate");
        task.estimatedMinutes = item.optInt("estimatedMinutes", 45);
        task.notes = item.optString("notes");
        task.effort = nullableString(item, "effort");
        task.discomfort = nullableString(item, "discomfort");
        task.discomfortNotes = nullableString(item, "discomfortNotes");
        task.feedbackNotes = nullableString(item, "feedbackNotes");
        task.createdAt = item.optLong("createdAt", System.currentTimeMillis());
        task.updatedAt = item.optLong("updatedAt", task.createdAt);
        task.startedAt = item.optLong("startedAt", 0);
        task.completedAt = item.optLong("completedAt", 0);
        task.deletedAt = item.optLong("deletedAt", 0);
        JSONArray exercises = item.optJSONArray("exercises");
        if (exercises != null) {
            for (int i = 0; i < exercises.length(); i++) {
                JSONObject exerciseJson = exercises.optJSONObject(i);
                if (exerciseJson == null) continue;
                WorkoutExercise exercise = new WorkoutExercise();
                exercise.id = exerciseJson.optString("id", UUID.randomUUID().toString().replace("-", ""));
                exercise.name = exerciseJson.optString("name", "未命名动作");
                exercise.restSeconds = exerciseJson.optInt("restSeconds", 90);
                exercise.notes = exerciseJson.optString("notes");
                exercise.isAdditional = exerciseJson.optBoolean("isAdditional", false);
                exercise.skipReason = nullableString(exerciseJson, "skipReason");
                JSONArray sets = exerciseJson.optJSONArray("sets");
                if (sets != null) {
                    for (int j = 0; j < sets.length(); j++) {
                        JSONObject setJson = sets.optJSONObject(j);
                        if (setJson == null) continue;
                        WorkoutSet set = new WorkoutSet();
                        set.index = setJson.optInt("index", j + 1);
                        set.targetWeight = setJson.optDouble("targetWeight", 0);
                        set.targetReps = setJson.optInt("targetReps", 0);
                        set.actualWeight = setJson.optDouble("actualWeight", set.targetWeight);
                        set.actualReps = setJson.optInt("actualReps", set.targetReps);
                        set.completed = setJson.optBoolean("completed", false);
                        set.completedAt = setJson.optLong("completedAt", 0);
                        set.rir = nullableString(setJson, "rir");
                        set.movementQuality = nullableString(setJson, "movementQuality");
                        set.discomfort = nullableString(setJson, "discomfort");
                        set.discomfortNotes = nullableString(setJson, "discomfortNotes");
                        exercise.sets.add(set);
                    }
                }
                task.exercises.add(exercise);
            }
        }
        return task;
    }

    private String preview(String content) {
        String value = content.trim().replaceAll("\\s+", " ");
        return value.length() > 100 ? value.substring(0, 100) + "…" : value;
    }

    private static String defaultNoteTitle(String content) {
        for (String line : content.split("\\r?\\n")) {
            String value = line.trim();
            if (value.startsWith("☐") || value.startsWith("☑")) value = value.substring(1).trim();
            if (!value.isEmpty()) return value.length() > 24 ? value.substring(0, 24) + "…" : value;
        }
        return "未命名文本";
    }

    private static int normalizeFontSize(int size) {
        return Math.max(12, Math.min(32, size));
    }

    private static float normalizeLineSpacing(double value) {
        return Math.round(Math.max(1.0, Math.min(2.0, value)) * 10) / 10f;
    }

    private static float normalizeLetterSpacing(double value) {
        return Math.round(Math.max(0.0, Math.min(0.16, value)) * 100) / 100f;
    }

    private static String formatDecimal(float value) {
        String formatted = String.format(Locale.CHINA, "%.2f", value);
        while (formatted.endsWith("0")) formatted = formatted.substring(0, formatted.length() - 1);
        return formatted.endsWith(".") ? formatted.substring(0, formatted.length() - 1) : formatted;
    }

    private static final class Note {
        final String id;
        String title;
        String content;
        int fontSize;
        float lineSpacing;
        float letterSpacing;
        long updatedAt;

        Note(String id, String title, String content, int fontSize, long updatedAt) {
            this(id, title, content, fontSize, 1.3f, 0.02f, updatedAt);
        }

        Note(String id, String title, String content, int fontSize, float lineSpacing,
             float letterSpacing, long updatedAt) {
            this.id = id;
            this.title = title;
            this.content = content;
            this.fontSize = normalizeFontSize(fontSize);
            this.lineSpacing = normalizeLineSpacing(lineSpacing);
            this.letterSpacing = normalizeLetterSpacing(letterSpacing);
            this.updatedAt = updatedAt;
        }
    }

    private static final class WorkoutTask {
        String id = "";
        String title = "未命名训练";
        String sourceProject = "D";
        String status = "pending";
        String previousStatus = "";
        String scheduledDate = "";
        int estimatedMinutes = 45;
        String notes = "";
        String effort;
        String discomfort;
        String discomfortNotes;
        String feedbackNotes;
        long createdAt;
        long updatedAt;
        long startedAt;
        long completedAt;
        long deletedAt;
        final List<WorkoutExercise> exercises = new ArrayList<>();
    }

    private static final class WorkoutExercise {
        String id = "";
        String name = "未命名动作";
        int restSeconds = 90;
        String notes = "";
        boolean isAdditional;
        String skipReason;
        final List<WorkoutSet> sets = new ArrayList<>();
    }

    private static final class WorkoutSet {
        int index;
        double targetWeight;
        int targetReps;
        double actualWeight;
        int actualReps;
        boolean completed;
        long completedAt;
        String rir;
        String movementQuality;
        String discomfort;
        String discomfortNotes;
    }

    private void startQrScan() {
        suppressAutoReconnect = true;
        disconnectInternal();
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            appendLog("正在请求相机权限……");
            requestPermissions(new String[]{Manifest.permission.CAMERA}, REQUEST_CAMERA);
            return;
        }
        launchQrScanner();
    }

    private void launchQrScanner() {
        new IntentIntegrator(this)
                .setCaptureActivity(LanDropCaptureActivity.class)
                .setDesiredBarcodeFormats(IntentIntegrator.QR_CODE)
                .setPrompt("扫描电脑端的配对二维码")
                .setBeepEnabled(false)
                .setOrientationLocked(false)
                .initiateScan();
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != REQUEST_CAMERA) return;
        if (grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
            appendLog("相机权限已允许，正在打开扫码页面……");
            launchQrScanner();
        } else {
            appendLog("相机权限被拒绝，无法扫描二维码");
            toast("需要相机权限才能扫码；你也可以在系统设置中手动允许。 ");
            suppressAutoReconnect = false;
            startAutoDiscovery();
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        IntentResult scanResult = IntentIntegrator.parseActivityResult(requestCode, resultCode, data);
        if (scanResult != null) {
            if (scanResult.getContents() != null) {
                connectFromQr(scanResult.getContents());
            } else {
                suppressAutoReconnect = false;
                startAutoDiscovery();
            }
            return;
        }
        if (requestCode == REQUEST_FILE && resultCode == RESULT_OK && data != null && data.getData() != null) {
            Uri uri = data.getData();
            executor.execute(() -> prepareAndSendFile(uri));
            return;
        }
        super.onActivityResult(requestCode, resultCode, data);
    }

    private void connectFromQr(String value) {
        try {
            Uri uri = Uri.parse(value);
            if (!"landrop".equals(uri.getScheme()) || !"pair".equals(uri.getHost()))
                throw new IllegalArgumentException("这不是局域互传二维码");
            String host = uri.getQueryParameter("host");
            String token = uri.getQueryParameter("token");
            String device = uri.getQueryParameter("device");
            int port = Integer.parseInt(uri.getQueryParameter("port"));
            if (host == null || token == null) throw new IllegalArgumentException("二维码信息不完整");
            setStatus("正在连接 " + host + "……", false, device);
            executor.execute(() -> connect(host, port, token, device, true, null));
        } catch (Exception e) {
            toast("二维码无效：" + e.getMessage());
            suppressAutoReconnect = false;
            startAutoDiscovery();
        }
    }

    private void connect(String host, int port, String token, String peerName, boolean qrPairing,
                         String expectedServerProof) {
        disconnectInternal();
        try {
            Socket newSocket = new Socket();
            newSocket.connect(new InetSocketAddress(host, port), 8000);
            newSocket.setTcpNoDelay(true);
            DataInputStream newInput = new DataInputStream(new BufferedInputStream(newSocket.getInputStream()));
            DataOutputStream newOutput = new DataOutputStream(new BufferedOutputStream(newSocket.getOutputStream()));
            socket = newSocket;
            input = newInput;
            output = newOutput;

            JSONObject hello = new JSONObject();
            hello.put("type", "hello");
            hello.put("token", token);
            hello.put("payloadLength", 0);
            hello.put("deviceId", deviceId);
            hello.put("deviceName", Build.MANUFACTURER + " " + Build.MODEL);
            sendHeaderAndPayload(hello, null, 0);

            JSONObject ack = readHeader(newInput);
            if (!"hello_ack".equals(ack.optString("type"))) throw new Exception("电脑拒绝了连接");
            String windowsId = ack.optString("deviceId");
            if (windowsId.isEmpty()) throw new Exception("电脑身份无效");
            if (qrPairing) {
                String pairKey = ack.optString("token");
                if (pairKey.isEmpty()) throw new Exception("电脑未返回配对密钥");
                getPreferences(MODE_PRIVATE).edit()
                        .putString(PAIRED_WINDOWS_ID_KEY, windowsId)
                        .putString(PAIRED_WINDOWS_NAME_KEY, peerName == null ? host : peerName)
                        .putString(PAIR_KEY_KEY, pairKey)
                        .apply();
                suppressAutoReconnect = false;
            } else {
                String pairedWindowsId = getPreferences(MODE_PRIVATE)
                        .getString(PAIRED_WINDOWS_ID_KEY, "");
                if (!windowsId.equals(pairedWindowsId) || expectedServerProof == null ||
                        !expectedServerProof.equalsIgnoreCase(ack.optString("token")))
                    throw new Exception("自动连接身份验证失败");
            }
            String connectedName = peerName == null ? host : peerName;
            setStatus("设备已连接", true, connectedName);
            appendLog((qrPairing ? "已配对电脑：" : "已自动连接电脑：") + connectedName);
            readLoop(newSocket, newInput);
        } catch (Exception e) {
            disconnectInternal();
            if (qrPairing) {
                setStatus("连接失败", false, e.getMessage());
                appendLog("连接失败：" + e.getMessage());
            } else if (!suppressAutoReconnect && !destroyed) {
                setStatus("正在寻找已配对电脑", false, "请确保电脑端已打开并位于同一局域网");
            }
        }
        if (qrPairing) {
            suppressAutoReconnect = false;
            if (!destroyed) startAutoDiscovery();
        }
    }

    private void startAutoDiscovery() {
        String pairedWindowsId = getPreferences(MODE_PRIVATE).getString(PAIRED_WINDOWS_ID_KEY, "");
        String pairKey = getPreferences(MODE_PRIVATE).getString(PAIR_KEY_KEY, "");
        synchronized (autoDiscoveryLock) {
            if (pairedWindowsId.isEmpty() || pairKey.isEmpty() || connected || suppressAutoReconnect ||
                    destroyed || autoDiscoveryRunning) return;
            autoDiscoveryRunning = true;
        }
        setStatus("正在寻找已配对电脑", false, "请确保电脑端已打开并位于同一局域网");
        executor.execute(() -> {
            try {
                while (!destroyed && !suppressAutoReconnect && !connected) {
                    discoverAndConnect(pairedWindowsId, pairKey);
                    if (!destroyed && !suppressAutoReconnect && !connected) Thread.sleep(3000);
                }
            } catch (InterruptedException ignored) {
                Thread.currentThread().interrupt();
            } finally {
                synchronized (autoDiscoveryLock) {
                    autoDiscoveryRunning = false;
                }
                if (!destroyed && !suppressAutoReconnect && !connected) startAutoDiscovery();
            }
        });
    }

    private void discoverAndConnect(String pairedWindowsId, String pairKey) {
        try (DatagramSocket discovery = new DatagramSocket()) {
            discovery.setBroadcast(true);
            discovery.setSoTimeout(1800);
            JSONObject request = new JSONObject();
            request.put("type", "landrop_discover");
            request.put("deviceId", deviceId);
            byte[] requestBytes = request.toString().getBytes(StandardCharsets.UTF_8);
            discovery.send(new DatagramPacket(requestBytes, requestBytes.length,
                    InetAddress.getByName("255.255.255.255"), DISCOVERY_PORT));

            byte[] buffer = new byte[4096];
            while (!destroyed && !suppressAutoReconnect && !connected) {
                DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
                discovery.receive(packet);
                JSONObject offer = new JSONObject(new String(packet.getData(), packet.getOffset(),
                        packet.getLength(), StandardCharsets.UTF_8));
                if (!"landrop_offer".equals(offer.optString("type")) ||
                        !pairedWindowsId.equals(offer.optString("deviceId"))) continue;
                String nonce = offer.optString("nonce");
                int port = offer.optInt("port", -1);
                if (nonce.isEmpty() || port <= 0 || port > 65535) continue;
                String proof = computeDiscoveryProof(pairKey, nonce);
                String serverProof = computeDiscoveryProof(pairKey, nonce + ":server");
                synchronized (autoDiscoveryLock) {
                    if (autoConnectInProgress || connected || suppressAutoReconnect || destroyed) return;
                    autoConnectInProgress = true;
                }
                try {
                    connect(packet.getAddress().getHostAddress(), port, proof,
                            offer.optString("deviceName", packet.getAddress().getHostAddress()),
                            false, serverProof);
                } finally {
                    synchronized (autoDiscoveryLock) {
                        autoConnectInProgress = false;
                    }
                }
                return;
            }
        } catch (SocketTimeoutException ignored) {
        } catch (Exception ignored) {
        }
    }

    private static String computeDiscoveryProof(String pairKey, String nonce) throws Exception {
        Mac hmac = Mac.getInstance("HmacSHA256");
        hmac.init(new SecretKeySpec(hexToBytes(pairKey), "HmacSHA256"));
        byte[] proof = hmac.doFinal(nonce.getBytes(StandardCharsets.UTF_8));
        StringBuilder result = new StringBuilder(proof.length * 2);
        for (byte value : proof) result.append(String.format(Locale.ROOT, "%02X", value & 0xff));
        return result.toString();
    }

    private static byte[] hexToBytes(String value) throws Exception {
        if ((value.length() & 1) != 0) throw new Exception("配对密钥无效");
        byte[] result = new byte[value.length() / 2];
        for (int i = 0; i < result.length; i++) {
            int high = Character.digit(value.charAt(i * 2), 16);
            int low = Character.digit(value.charAt(i * 2 + 1), 16);
            if (high < 0 || low < 0) throw new Exception("配对密钥无效");
            result[i] = (byte) ((high << 4) | low);
        }
        return result;
    }

    private void readLoop(Socket expectedSocket, DataInputStream stream) {
        try {
            while (!expectedSocket.isClosed() && expectedSocket == socket) {
                JSONObject header = readHeader(stream);
                String type = header.optString("type");
                if ("text".equals(type)) {
                    appendLog("电脑：" + header.optString("message"));
                } else if ("file".equals(type)) {
                    receiveFile(stream, header.optString("name", "未命名文件"), header.optLong("payloadLength", -1));
                } else if ("notes_sync".equals(type)) {
                    receiveNotesSync(stream, header.optLong("payloadLength", -1));
                    sendNotesSnapshot("notes_sync_reply", false);
                } else if ("notes_sync_reply".equals(type)) {
                    receiveNotesSync(stream, header.optLong("payloadLength", -1));
                } else if ("sync_all".equals(type)) {
                    receiveAllSync(stream, header.optLong("payloadLength", -1));
                    sendAllSnapshot("sync_all_reply", false);
                } else if ("sync_all_reply".equals(type)) {
                    receiveAllSync(stream, header.optLong("payloadLength", -1));
                } else if ("pong".equals(type)) {
                    // Keep-alive response; no UI update needed.
                } else {
                    throw new Exception("未知消息类型：" + type);
                }
            }
        } catch (Exception e) {
            if (expectedSocket == socket) disconnect("连接已断开：" + e.getMessage());
        }
    }

    private void sendText() {
        String value = messageEdit.getText().toString().trim();
        if (value.isEmpty()) return;
        executor.execute(() -> {
            try {
                JSONObject header = baseHeader("text", 0);
                header.put("message", value);
                sendHeaderAndPayload(header, null, 0);
                runOnUiThread(() -> messageEdit.setText(""));
                appendLog("手机：" + value);
            } catch (Exception e) {
                appendLog("发送失败：" + e.getMessage());
            }
        });
    }

    private void syncNotes() {
        executor.execute(() -> {
            try {
                sendNotesSnapshot("notes_sync", true);
            } catch (Exception e) {
                appendLog("文本同步失败：" + e.getMessage());
            }
        });
    }

    private void syncAll() {
        if (!connected) {
            toast("尚未连接电脑，无法同步");
            return;
        }
        executor.execute(() -> {
            try {
                sendAllSnapshot("sync_all", true);
            } catch (Exception e) {
                appendLog("同步全部失败：" + e.getMessage());
                toast("同步失败，请检查连接");
            }
        });
    }

    private void sendAllSnapshot(String type, boolean reportToUser) throws Exception {
        persistEditingNoteOnUiThread();
        JSONObject envelope = new JSONObject();
        envelope.put("notes", notesToJson(notes));
        envelope.put("tasks", tasksToJson());
        byte[] payload = envelope.toString().getBytes(StandardCharsets.UTF_8);
        if (payload.length > 32 * 1024 * 1024) throw new Exception("同步数据过多");
        JSONObject header = baseHeader(type, payload.length);
        header.put("name", "sync-all.json");
        sendHeaderAndPayload(header, new java.io.ByteArrayInputStream(payload), payload.length);
        if (reportToUser) appendLog("已发送文本记录和任务，等待电脑合并");
    }

    private void receiveAllSync(DataInputStream stream, long length) throws Exception {
        if (length < 0 || length > 32L * 1024 * 1024) throw new Exception("同步数据大小无效");
        byte[] payload = new byte[(int) length];
        stream.readFully(payload);
        JSONObject envelope = new JSONObject(new String(payload, StandardCharsets.UTF_8));
        persistEditingNoteOnUiThread();
        int noteChanges = mergeNotes(envelope.optJSONArray("notes"));
        int taskChanges = mergeTasks(envelope.optJSONArray("tasks"));
        if (noteChanges > 0) saveNotes();
        if (taskChanges > 0) saveTasks();
        final int mergedNotes = noteChanges;
        final int mergedTasks = taskChanges;
        runOnUiThread(() -> {
            if (activeWorkoutTask == null && (activeTab == TAB_TEXT || activeTab == TAB_TASK)) showTab(activeTab);
            String result = "同步完成：文本 " + mergedNotes + " 条，任务 " + mergedTasks + " 项";
            appendLog(result);
            toast(result);
        });
    }

    private int mergeNotes(JSONArray incoming) {
        if (incoming == null) return 0;
        int changed = 0;
        for (int i = 0; i < incoming.length(); i++) {
            JSONObject item = incoming.optJSONObject(i);
            if (item == null) continue;
            String id = item.optString("id");
            String title = item.optString("title");
            String content = item.optString("content");
            int fontSize = normalizeFontSize(item.optInt("fontSize", 18));
            float lineSpacing = normalizeLineSpacing(item.optDouble("lineSpacing", 1.3));
            float letterSpacing = normalizeLetterSpacing(item.optDouble("letterSpacing", 0.02));
            long updatedAt = item.optLong("updatedAt", 0);
            if (id.isEmpty() || content.trim().isEmpty()) continue;
            Note existing = null;
            for (Note note : notes) if (id.equals(note.id)) { existing = note; break; }
            if (existing != null && existing.updatedAt > updatedAt) continue;
            if (existing == null) {
                notes.add(0, new Note(id, title.isEmpty() ? defaultNoteTitle(content) : title, content,
                        fontSize, lineSpacing, letterSpacing,
                        updatedAt > 0 ? updatedAt : System.currentTimeMillis()));
            } else {
                if (!title.isEmpty()) existing.title = title;
                if (item.has("fontSize")) existing.fontSize = fontSize;
                if (item.has("lineSpacing")) existing.lineSpacing = lineSpacing;
                if (item.has("letterSpacing")) existing.letterSpacing = letterSpacing;
                existing.content = content;
                existing.updatedAt = updatedAt > 0 ? updatedAt : System.currentTimeMillis();
            }
            changed++;
        }
        return changed;
    }

    private int mergeTasks(JSONArray incoming) {
        if (incoming == null) return 0;
        int changed = 0;
        for (int i = 0; i < incoming.length(); i++) {
            JSONObject item = incoming.optJSONObject(i);
            if (item == null) continue;
            WorkoutTask task = taskFromJson(item);
            if (task == null) continue;
            WorkoutTask existing = null;
            for (WorkoutTask candidate : tasks) if (task.id.equals(candidate.id)) { existing = candidate; break; }
            if (existing != null && existing.updatedAt > task.updatedAt) continue;
            if (existing != null) tasks.set(tasks.indexOf(existing), task);
            else tasks.add(0, task);
            changed++;
        }
        return changed;
    }

    private void sendNotesSnapshot(String type, boolean reportToUser) throws Exception {
        persistEditingNoteOnUiThread();
        byte[] payload = notesToJson(notes).toString().getBytes(StandardCharsets.UTF_8);
        if (payload.length > 16 * 1024 * 1024) throw new Exception("文本记录过多，无法同步");
        JSONObject header = baseHeader(type, payload.length);
        header.put("name", "notes.json");
        sendHeaderAndPayload(header, new java.io.ByteArrayInputStream(payload), payload.length);
        if (reportToUser) appendLog("已发送 " + notes.size() + " 条文本记录，等待电脑合并");
    }

    private void receiveNotesSync(DataInputStream stream, long length) throws Exception {
        if (length < 0 || length > 16L * 1024 * 1024) throw new Exception("文本同步数据大小无效");
        byte[] payload = new byte[(int) length];
        stream.readFully(payload);
        JSONArray incoming = new JSONArray(new String(payload, StandardCharsets.UTF_8));
        persistEditingNoteOnUiThread();
        int changed = 0;
        for (int i = 0; i < incoming.length(); i++) {
            JSONObject item = incoming.getJSONObject(i);
            String id = item.optString("id");
            String title = item.optString("title");
            String content = item.optString("content");
            int fontSize = normalizeFontSize(item.optInt("fontSize", 18));
            float lineSpacing = normalizeLineSpacing(item.optDouble("lineSpacing", 1.3));
            float letterSpacing = normalizeLetterSpacing(item.optDouble("letterSpacing", 0.02));
            long updatedAt = item.optLong("updatedAt", 0);
            if (id.isEmpty() || content.trim().isEmpty()) continue;
            Note existing = null;
            for (Note note : notes) {
                if (id.equals(note.id)) { existing = note; break; }
            }
            if (existing != null && existing.updatedAt > updatedAt) continue;
            if (existing == null) {
                notes.add(0, new Note(id, title.isEmpty() ? defaultNoteTitle(content) : title, content,
                        fontSize, lineSpacing, letterSpacing,
                        updatedAt > 0 ? updatedAt : System.currentTimeMillis()));
            } else {
                if (!title.isEmpty()) existing.title = title;
                if (item.has("fontSize")) existing.fontSize = fontSize;
                if (item.has("lineSpacing")) existing.lineSpacing = lineSpacing;
                if (item.has("letterSpacing")) existing.letterSpacing = letterSpacing;
                existing.content = content;
                existing.updatedAt = updatedAt > 0 ? updatedAt : System.currentTimeMillis();
            }
            changed++;
        }
        if (changed > 0) saveNotes();
        final int merged = changed;
        runOnUiThread(() -> {
            if (activeTab == TAB_TEXT && editingNote == null) showTab(TAB_TEXT);
            appendLog("文本同步完成：合并 " + merged + " 条电脑记录");
        });
    }

    private void chooseFile() {
        Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        intent.addCategory(Intent.CATEGORY_OPENABLE);
        intent.setType("*/*");
        startActivityForResult(intent, REQUEST_FILE);
    }

    private void prepareAndSendFile(Uri uri) {
        File temp = new File(getCacheDir(), "send-" + UUID.randomUUID());
        String name = queryDisplayName(uri);
        try (InputStream source = getContentResolver().openInputStream(uri);
             OutputStream target = new FileOutputStream(temp)) {
            if (source == null) throw new Exception("无法打开所选文件");
            copy(source, target, -1);
            try (InputStream payload = new FileInputStream(temp)) {
                JSONObject header = baseHeader("file", temp.length());
                header.put("name", name);
                sendHeaderAndPayload(header, payload, temp.length());
            }
            appendLog("已发送文件：" + name + "（" + formatBytes(temp.length()) + "）");
        } catch (Exception e) {
            appendLog("发送失败：" + e.getMessage());
        } finally {
            //noinspection ResultOfMethodCallIgnored
            temp.delete();
        }
    }

    private void receiveFile(DataInputStream stream, String unsafeName, long length) throws Exception {
        if (length < 0 || length > MAX_PAYLOAD) throw new Exception("文件大小超出限制");
        String name = sanitizeName(unsafeName);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ContentValues values = new ContentValues();
            values.put(MediaStore.Downloads.DISPLAY_NAME, name);
            values.put(MediaStore.Downloads.MIME_TYPE, "application/octet-stream");
            values.put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/局域投送");
            values.put(MediaStore.Downloads.IS_PENDING, 1);
            Uri item = getContentResolver().insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
            if (item == null) throw new Exception("无法创建接收文件");
            try (OutputStream target = getContentResolver().openOutputStream(item)) {
                if (target == null) throw new Exception("无法写入接收文件");
                copy(stream, target, length);
            } catch (Exception e) {
                getContentResolver().delete(item, null, null);
                throw e;
            }
            values.clear();
            values.put(MediaStore.Downloads.IS_PENDING, 0);
            getContentResolver().update(item, values, null, null);
        } else {
            File directory = new File(getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS), "局域投送");
            //noinspection ResultOfMethodCallIgnored
            directory.mkdirs();
            try (OutputStream target = new FileOutputStream(uniqueFile(directory, name))) {
                copy(stream, target, length);
            }
        }
        appendLog("已接收文件：" + name + "（" + formatBytes(length) + "）");
    }

    private void sendHeaderAndPayload(JSONObject header, InputStream payload, long length) throws Exception {
        DataOutputStream current = output;
        if (current == null) throw new Exception("设备尚未连接");
        byte[] json = header.toString().getBytes(StandardCharsets.UTF_8);
        synchronized (sendLock) {
            current.writeInt(json.length);
            current.write(json);
            if (payload != null) copy(payload, current, length);
            current.flush();
        }
    }

    private JSONObject readHeader(DataInputStream stream) throws Exception {
        int length;
        try {
            length = stream.readInt();
        } catch (EOFException e) {
            throw new EOFException("电脑已关闭连接");
        }
        if (length <= 0 || length > 1024 * 1024) throw new Exception("消息头长度无效");
        byte[] json = new byte[length];
        stream.readFully(json);
        return new JSONObject(new String(json, StandardCharsets.UTF_8));
    }

    private JSONObject baseHeader(String type, long payloadLength) throws Exception {
        JSONObject header = new JSONObject();
        header.put("type", type);
        header.put("payloadLength", payloadLength);
        header.put("deviceId", deviceId);
        return header;
    }

    private static void copy(InputStream source, OutputStream target, long exactLength) throws Exception {
        byte[] buffer = new byte[81920];
        if (exactLength < 0) {
            int count;
            while ((count = source.read(buffer)) != -1) target.write(buffer, 0, count);
            return;
        }
        long remaining = exactLength;
        while (remaining > 0) {
            int count = source.read(buffer, 0, (int) Math.min(buffer.length, remaining));
            if (count == -1) throw new EOFException("文件传输中断");
            target.write(buffer, 0, count);
            remaining -= count;
        }
    }

    private String queryDisplayName(Uri uri) {
        try (android.database.Cursor cursor = getContentResolver().query(uri, null, null, null, null)) {
            if (cursor != null && cursor.moveToFirst()) {
                int index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                if (index >= 0) return sanitizeName(cursor.getString(index));
            }
        } catch (Exception ignored) {
        }
        return "文件-" + new SimpleDateFormat("yyyyMMdd-HHmmss", Locale.CHINA).format(new Date());
    }

    private void disconnect(String reason) {
        disconnectInternal();
        boolean hasPairedComputer = !getPreferences(MODE_PRIVATE).getString(PAIR_KEY_KEY, "").isEmpty();
        setStatus("尚未连接", false, hasPairedComputer && !suppressAutoReconnect
                ? "正在等待已配对电脑" : "请重新扫描电脑端二维码");
        appendLog(reason);
    }

    private void disconnectInternal() {
        Socket current = socket;
        socket = null;
        input = null;
        output = null;
        if (current != null) try { current.close(); } catch (Exception ignored) { }
    }

    private void setStatus(String status, boolean connected, String detail) {
        this.connected = connected;
        runOnUiThread(() -> {
            this.connectionStatus = status;
            this.connectionDetail = detail == null ? "" : detail;
            this.connected = connected;
            if (statusText != null) {
                statusText.setText(status);
                statusText.setTextColor(connected ? connectionColor() : PRIMARY);
            }
            if (peerText != null) peerText.setText(this.connectionDetail);
            if (sendTextButton != null) sendTextButton.setEnabled(connected);
            if (sendFileButton != null) sendFileButton.setEnabled(connected);
            if (disconnectButton != null) disconnectButton.setEnabled(connected);
            if (syncAllButton != null) {
                syncAllButton.setEnabled(connected);
                syncAllButton.setTextColor(connected ? BACKGROUND : TEXT_SECONDARY);
                syncAllButton.setBackground(rounded(connected ? PRIMARY : SURFACE_ACTIVE, 14));
            }
        });
    }

    private void appendLog(String line) {
        runOnUiThread(() -> {
            String time = new SimpleDateFormat("HH:mm:ss", Locale.CHINA).format(new Date());
            transferLog.append("\n[").append(time).append("] ").append(line);
            if (logText != null) logText.setText(transferLog.toString());
        });
    }

    private void toast(String text) {
        runOnUiThread(() -> Toast.makeText(this, text, Toast.LENGTH_LONG).show());
    }

    private TextView text(String value, int sp, int color) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(sp);
        view.setTextColor(color);
        return view;
    }

    private Button button(String value) {
        Button button = new Button(this);
        button.setText(value);
        button.setTextSize(14);
        button.setAllCaps(false);
        button.setMinHeight(0);
        button.setMinimumHeight(0);
        button.setMinWidth(0);
        button.setMinimumWidth(0);
        button.setPadding(dp(10), 0, dp(10), 0);
        button.setTextColor(TEXT_PRIMARY);
        button.setBackground(rounded(SURFACE_ACTIVE, 8));
        return button;
    }

    private LinearLayout dialogPanel(String titleValue, String message) {
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(22), dp(20), dp(22), dp(18));
        panel.setBackground(rounded(SURFACE, 20));
        TextView title = text(titleValue, 20, TEXT_PRIMARY);
        panel.addView(title, matchWrap());
        if (message != null && !message.isEmpty()) {
            TextView detail = text(message, 14, TEXT_SECONDARY);
            detail.setPadding(0, dp(8), 0, dp(16));
            panel.addView(detail, matchWrap());
        } else {
            panel.setPadding(dp(22), dp(20), dp(22), dp(18));
        }
        return panel;
    }

    private Button dialogButton(String value, boolean destructive) {
        Button button = button(value);
        button.setTextColor(destructive ? Color.WHITE : TEXT_PRIMARY);
        button.setBackground(rounded(destructive ? Color.rgb(190, 64, 61) : SURFACE_ACTIVE, 12));
        return button;
    }

    private void showThemedDialog(Dialog dialog, View panel) {
        dialog.setContentView(panel);
        dialog.setCanceledOnTouchOutside(true);
        dialog.show();
        Window window = dialog.getWindow();
        if (window == null) return;
        WindowCompat.getInsetsController(window, window.getDecorView())
                .setAppearanceLightStatusBars("Silver".equals(visualTheme));
        WindowCompat.getInsetsController(window, window.getDecorView())
                .setAppearanceLightNavigationBars("Silver".equals(visualTheme));
        window.setBackgroundDrawable(new ColorDrawable(Color.TRANSPARENT));
        window.addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND);
        WindowManager.LayoutParams attributes = window.getAttributes();
        attributes.dimAmount = "Silver".equals(visualTheme) ? 0.28f : 0.62f;
        window.setAttributes(attributes);
        window.setLayout(getResources().getDisplayMetrics().widthPixels - dp(40),
                WindowManager.LayoutParams.WRAP_CONTENT);
    }

    private LinearLayout.LayoutParams matchWrap() {
        return new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
    }

    private LinearLayout.LayoutParams weighted() {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        params.setMargins(dp(2), 0, dp(2), 0);
        return params;
    }

    private LinearLayout.LayoutParams weighted(int height) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0, height, 1f);
        params.setMargins(dp(2), 0, dp(2), 0);
        return params;
    }

    private LinearLayout.LayoutParams fixed(int width, int height) {
        return new LinearLayout.LayoutParams(width, height);
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private static String sanitizeName(String name) {
        if (name == null || name.trim().isEmpty()) return "未命名文件";
        return name.replace("/", "_").replace("\\", "_").replace("\u0000", "_");
    }

    private static File uniqueFile(File directory, String name) {
        File result = new File(directory, name);
        if (!result.exists()) return result;
        int dot = name.lastIndexOf('.');
        String stem = dot > 0 ? name.substring(0, dot) : name;
        String ext = dot > 0 ? name.substring(dot) : "";
        for (int i = 1; ; i++) {
            result = new File(directory, stem + " (" + i + ")" + ext);
            if (!result.exists()) return result;
        }
    }

    private static String formatBytes(long bytes) {
        if (bytes >= 1024L * 1024 * 1024) return String.format(Locale.CHINA, "%.1f 吉字节", bytes / 1024d / 1024 / 1024);
        if (bytes >= 1024L * 1024) return String.format(Locale.CHINA, "%.1f 兆字节", bytes / 1024d / 1024);
        if (bytes >= 1024) return String.format(Locale.CHINA, "%.1f 千字节", bytes / 1024d);
        return bytes + " 字节";
    }

    private static final class EditorState {
        final String text;
        final int selectionStart;
        final int selectionEnd;

        EditorState(String text, int selectionStart, int selectionEnd) {
            this.text = text;
            this.selectionStart = selectionStart;
            this.selectionEnd = selectionEnd;
        }
    }

    private final class ChecklistEditText extends EditText {
        private static final char TODO_OPEN = '\u2610';
        private static final char TODO_DONE = '\u2611';
        private static final int HISTORY_LIMIT = 80;
        private final Deque<EditorState> undoHistory = new ArrayDeque<>();
        private final Deque<EditorState> redoHistory = new ArrayDeque<>();
        private int pressedTodoStart = -1;
        private boolean restoringHistory;
        private EditorState beforeChange;
        private Runnable historyChangedListener;

        ChecklistEditText(Context context) {
            super(context);
            addTextChangedListener(new TextWatcher() {
                @Override
                public void beforeTextChanged(CharSequence text, int start, int count, int after) {
                    if (!restoringHistory) beforeChange = captureState(text);
                }
                @Override public void onTextChanged(CharSequence text, int start, int before, int count) { }
                @Override
                public void afterTextChanged(Editable text) {
                    refreshTodoSpans(text);
                    if (!restoringHistory && beforeChange != null
                            && !beforeChange.text.contentEquals(text)) {
                        undoHistory.addLast(beforeChange);
                        while (undoHistory.size() > HISTORY_LIMIT) undoHistory.removeFirst();
                        redoHistory.clear();
                    }
                    beforeChange = null;
                    notifyHistoryChanged();
                }
            });
        }

        void setInitialText(String value) {
            restoringHistory = true;
            setText(value);
            setSelection(length());
            undoHistory.clear();
            redoHistory.clear();
            beforeChange = null;
            restoringHistory = false;
        }

        void setHistoryChangedListener(Runnable listener) {
            historyChangedListener = listener;
            notifyHistoryChanged();
        }

        boolean canUndo() {
            return !undoHistory.isEmpty();
        }

        boolean canRedo() {
            return !redoHistory.isEmpty();
        }

        void performUndo() {
            if (!canUndo()) return;
            redoHistory.addLast(captureState(getText()));
            restoreState(undoHistory.removeLast());
        }

        void performRedo() {
            if (!canRedo()) return;
            undoHistory.addLast(captureState(getText()));
            restoreState(redoHistory.removeLast());
        }

        private EditorState captureState(CharSequence text) {
            int start = Math.max(0, getSelectionStart());
            int end = Math.max(0, getSelectionEnd());
            return new EditorState(text.toString(), start, end);
        }

        private void restoreState(EditorState state) {
            restoringHistory = true;
            setText(state.text);
            int start = Math.min(length(), state.selectionStart);
            int end = Math.min(length(), state.selectionEnd);
            setSelection(start, end);
            restoringHistory = false;
            notifyHistoryChanged();
        }

        private void notifyHistoryChanged() {
            if (historyChangedListener != null) historyChangedListener.run();
        }

        boolean isCurrentLineTodo() {
            Editable text = getText();
            return isTodoAt(text, currentLineStart(text));
        }

        void toggleTodoForCurrentLine() {
            Editable text = getText();
            int start = currentLineStart(text);
            int selection = Math.max(start, getSelectionStart());
            if (isTodoAt(text, start)) {
                int removeCount = start + 1 < text.length() && text.charAt(start + 1) == ' ' ? 2 : 1;
                text.delete(start, Math.min(text.length(), start + removeCount));
                setSelection(Math.max(start, selection - removeCount));
            } else {
                text.insert(start, TODO_OPEN + " ");
                setSelection(Math.min(text.length(), selection + 2));
            }
            requestFocus();
        }

        @Override
        public boolean onTouchEvent(MotionEvent event) {
            if (event.getActionMasked() == MotionEvent.ACTION_DOWN) {
                pressedTodoStart = touchedTodoStart(event);
                if (pressedTodoStart >= 0) return true;
            } else if (pressedTodoStart >= 0 && event.getActionMasked() == MotionEvent.ACTION_MOVE) {
                if (touchedTodoStart(event) != pressedTodoStart) pressedTodoStart = -1;
                return true;
            } else if (pressedTodoStart >= 0 && event.getActionMasked() == MotionEvent.ACTION_UP) {
                int start = pressedTodoStart;
                pressedTodoStart = -1;
                if (touchedTodoStart(event) == start) {
                    Editable text = getText();
                    if (isTodoAt(text, start)) {
                        text.replace(start, start + 1,
                                text.charAt(start) == TODO_DONE ? String.valueOf(TODO_OPEN) : String.valueOf(TODO_DONE));
                        setSelection(Math.min(text.length(), start + 2));
                    }
                }
                return true;
            } else if (pressedTodoStart >= 0 && event.getActionMasked() == MotionEvent.ACTION_CANCEL) {
                pressedTodoStart = -1;
                return true;
            }
            return super.onTouchEvent(event);
        }

        private int currentLineStart(Editable text) {
            int cursor = Math.max(0, Math.min(text.length(), getSelectionStart()));
            while (cursor > 0 && text.charAt(cursor - 1) != '\n') cursor--;
            return cursor;
        }

        private int touchedTodoStart(MotionEvent event) {
            Layout layout = getLayout();
            if (layout == null) return -1;
            int vertical = (int) event.getY() - getTotalPaddingTop() + getScrollY();
            int line = layout.getLineForVertical(Math.max(0, vertical));
            int start = layout.getLineStart(line);
            if (!isTodoAt(getText(), start)) return -1;
            float left = getTotalPaddingLeft() + layout.getPrimaryHorizontal(start) - getScrollX();
            return event.getX() >= left - dp(4) && event.getX() <= left + dp(30) ? start : -1;
        }

        private void refreshTodoSpans(Editable text) {
            for (TodoBoxSpan span : text.getSpans(0, text.length(), TodoBoxSpan.class)) {
                text.removeSpan(span);
            }
            for (CompletedTodoSpan span : text.getSpans(0, text.length(), CompletedTodoSpan.class)) {
                text.removeSpan(span);
            }
            int start = 0;
            while (start < text.length()) {
                int end = start;
                while (end < text.length() && text.charAt(end) != '\n') end++;
                if (isTodoAt(text, start)) {
                    boolean done = text.charAt(start) == TODO_DONE;
                    text.setSpan(new TodoBoxSpan(done), start, start + 1, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
                    if (done && start + 1 < end) {
                        text.setSpan(new CompletedTodoSpan(NOTE_EDITOR_SECONDARY), start + 1, end,
                                Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
                    }
                }
                start = end + 1;
            }
        }

        private boolean isTodoAt(CharSequence text, int start) {
            return start >= 0 && start < text.length()
                    && (text.charAt(start) == TODO_OPEN || text.charAt(start) == TODO_DONE);
        }
    }

    private final class TodoBoxSpan extends ReplacementSpan {
        private final boolean done;

        TodoBoxSpan(boolean done) {
            this.done = done;
        }

        @Override
        public int getSize(Paint paint, CharSequence text, int start, int end, Paint.FontMetricsInt metrics) {
            return dp(28);
        }

        @Override
        public void draw(Canvas canvas, CharSequence text, int start, int end, float x, int top,
                         int y, int bottom, Paint paint) {
            float size = dp(19);
            float left = x + dp(2);
            float boxTop = (top + bottom - size) / 2f;
            RectF box = new RectF(left, boxTop, left + size, boxTop + size);
            Paint boxPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
            boxPaint.setStrokeWidth(dp(2));
            boxPaint.setColor(done ? PRIMARY : NOTE_SECONDARY);
            boxPaint.setStyle(done ? Paint.Style.FILL : Paint.Style.STROKE);
            canvas.drawRoundRect(box, dp(5), dp(5), boxPaint);
            if (done) {
                boxPaint.setColor(BACKGROUND);
                boxPaint.setStyle(Paint.Style.STROKE);
                boxPaint.setStrokeCap(Paint.Cap.ROUND);
                boxPaint.setStrokeJoin(Paint.Join.ROUND);
                boxPaint.setStrokeWidth(dp(2));
                canvas.drawLine(left + dp(5), boxTop + dp(10), left + dp(8), boxTop + dp(13), boxPaint);
                canvas.drawLine(left + dp(8), boxTop + dp(13), left + dp(15), boxTop + dp(6), boxPaint);
            }
        }
    }

    private static final class CompletedTodoSpan extends ForegroundColorSpan {
        CompletedTodoSpan(int color) {
            super(color);
        }
    }

    private final class ScanIconView extends View {
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);

        ScanIconView(android.content.Context context) {
            super(context);
            paint.setColor(PRIMARY);
            paint.setStyle(Paint.Style.STROKE);
            paint.setStrokeWidth(dp(2));
            paint.setStrokeCap(Paint.Cap.ROUND);
        }

        @Override
        protected void onDraw(Canvas canvas) {
            super.onDraw(canvas);
            float s = getWidth();
            float d = dp(5), arm = dp(7);
            canvas.drawLine(d, d + arm, d, d, paint);
            canvas.drawLine(d, d, d + arm, d, paint);
            canvas.drawLine(s - d - arm, d, s - d, d, paint);
            canvas.drawLine(s - d, d, s - d, d + arm, paint);
            canvas.drawLine(d, s - d - arm, d, s - d, paint);
            canvas.drawLine(d, s - d, d + arm, s - d, paint);
            canvas.drawLine(s - d - arm, s - d, s - d, s - d, paint);
            canvas.drawLine(s - d, s - d - arm, s - d, s - d, paint);
            canvas.drawLine(dp(9), s / 2f, s - dp(9), s / 2f, paint);
        }
    }

    @Override
    protected void onPause() {
        persistEditingNote();
        super.onPause();
    }

    @Override
    protected void onResume() {
        super.onResume();
        startAutoDiscovery();
    }

    @Override
    protected void onDestroy() {
        destroyed = true;
        suppressAutoReconnect = true;
        if (restTimer != null) restTimer.cancel();
        disconnectInternal();
        executor.shutdownNow();
        super.onDestroy();
    }
}
