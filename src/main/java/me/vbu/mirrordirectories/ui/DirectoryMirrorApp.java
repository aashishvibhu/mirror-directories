package me.vbu.mirrordirectories.ui;

import javafx.application.Application;
import javafx.scene.Scene;
import javafx.stage.Stage;
import me.vbu.mirrordirectories.ui.views.MainView;

/**
 * JavaFX Application class for the Directory Mirror application.
 */
public class DirectoryMirrorApp extends Application {

    @Override
    public void start(Stage primaryStage) {
        // Create main view
        MainView mainView = new MainView();

        // Create scene
        Scene scene = new Scene(mainView.getRoot(), 900, 700);

        // Set up stage
        primaryStage.setTitle("Directory Mirror Tool");
        primaryStage.setScene(scene);
        primaryStage.show();
    }
}
