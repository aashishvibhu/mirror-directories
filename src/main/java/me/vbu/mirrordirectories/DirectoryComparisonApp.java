package me.vbu.mirrordirectories;

import java.io.File;
import java.util.List;
import java.util.Scanner;

/**
 * Application controller for the directory comparison tool.
 */
public class DirectoryComparisonApp {

    private final DirectoryComparator comparator;

    public DirectoryComparisonApp() {
        comparator = DirectoryComparator.getInstance();
    }

    /**
     * Starts the directory comparison application.
     */
    public void start() {
        System.out.println("Directory Comparison Tool");
        System.out.println("------------------------");

        // Get directory paths from user
        String[] paths = getDirectoryPaths();
        String sourcePath = paths[0];
        String destPath = paths[1];

        // Create File objects for the directories
        File sourceDir = new File(sourcePath);
        File destDir = new File(destPath);

        // Validate directories
        if (!comparator.isValidDirectory(sourceDir)) {
            System.err.println("Error: Source path is not a valid directory.");
            return;
        }

        if (!comparator.isValidDirectory(destDir)) {
            System.err.println("Error: Destination path is not a valid directory.");
            return;
        }

        // Compare directories and get missing items
        List<String> missingItems = comparator.compareDirectories(sourceDir, destDir, "");

        // Display results
        displayResults(missingItems);
    }

    /**
     * Gets the source and destination directory paths from user input.
     * This method is separated to make it easier to change the input mechanism in the future.
     *
     * @return An array containing [sourcePath, destinationPath]
     */
    private String[] getDirectoryPaths() {
        try (Scanner scanner = new Scanner(System.in)) {
            // Get source directory path
            System.out.print("Enter source directory path: ");
            String sourcePath = scanner.nextLine();

            // Get destination directory path
            System.out.print("Enter destination directory path: ");
            String destPath = scanner.nextLine();

            return new String[]{sourcePath, destPath};
        }
    }

    /**
     * Displays the comparison results to the user.
     *
     * @param missingItems List of items missing from the destination
     */
    private void displayResults(List<String> missingItems) {
        if (missingItems.isEmpty()) {
            System.out.println("\nAll files and directories from source exist in destination.");
        } else {
            System.out.println("\nItems present in source but missing from destination:");
            for (String item : missingItems) {
                System.out.println(" - " + item);
            }
            System.out.println("\nTotal missing items: " + missingItems.size());
        }
    }
}
