package me.vbu.mirrordirectories.ui.views.components;

import javafx.geometry.Insets;
import javafx.geometry.Pos;
import javafx.scene.control.Button;
import javafx.scene.control.Label;
import javafx.scene.layout.HBox;
import javafx.scene.layout.Priority;
import lombok.Setter;

import java.util.function.Consumer;

/**
 * Component for handling bottom controls like Copy All button and status label.
 */
public class ControlPanel extends HBox {

    private Button copyAllButton;
    private Label statusLabel;

    /**
     * -- SETTER --
     *  Sets the callback for when the Copy All button is clicked
     *
     */
    @Setter
    private Consumer<Void> onCopyAllCallback;

    public ControlPanel() {
        initializeUI();
    }

    private void initializeUI() {
        this.setSpacing(10);
        this.setPadding(new Insets(10, 0, 0, 0));
        this.setAlignment(Pos.CENTER_LEFT);

        // Copy All button
        copyAllButton = new Button("Copy All Missing Files");
        copyAllButton.setOnAction(e -> {
            if (onCopyAllCallback != null) {
                onCopyAllCallback.accept(null);
            }
        });

        // Initial state is disabled until comparison is done
        copyAllButton.setDisable(true);

        // Status label
        statusLabel = new Label("Ready");
        HBox.setHgrow(statusLabel, Priority.ALWAYS);

        this.getChildren().addAll(copyAllButton, statusLabel);
    }

    /**
     * Sets the status message to display
     *
     * @param message Status message to display
     */
    public void setStatusMessage(String message) {
        statusLabel.setText(message);
    }

    /**
     * Enables or disables the Copy All button
     *
     * @param enabled Whether the button should be enabled
     */
    public void setCopyButtonEnabled(boolean enabled) {
        copyAllButton.setDisable(!enabled);
    }
}
