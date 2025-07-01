package me.vbu.mirrordirectories;

import me.vbu.mirrordirectories.model.DirectoryNode;
import me.vbu.mirrordirectories.model.Node;
import me.vbu.mirrordirectories.service.DirectoryComparator;

import java.io.File;
import java.io.IOException;
import java.util.Scanner;

/**
 * Application controller for the directory comparison tool.
 */
public class DirectoryComparisonApp {

    private final DirectoryComparator comparator;
    private final Scanner scanner;

    public DirectoryComparisonApp() {
        comparator = DirectoryComparator.getInstance();
        scanner = new Scanner(System.in);
    }

    /**
     * Starts the directory comparison application.
     */
    public void start() {
        System.out.println("Directory Comparison Tool");
        System.out.println("------------------------");

        try {
            // Get directory paths from user
            String[] paths = getDirectoryPaths();
            String sourcePath = paths[0];
            String destPath = paths[1];

            // Create File objects for the directories
            File sourceDir = new File(sourcePath);
            File destDir = new File(destPath);

            // Validate directories
            if (!isValidDirectory(sourceDir)) {
                System.err.println("Error: Source path is not a valid directory.");
                return;
            }

            if (!isValidDirectory(destDir)) {
                System.err.println("Error: Destination path is not a valid directory.");
                return;
            }

            // Compare directories and get missing items as a hierarchical structure
            DirectoryNode comparisonResult = comparator.compareDirectories(sourceDir, destDir);

            // Display results
            displayResults(comparisonResult, sourceDir, destDir);
        } finally {
            // Close the scanner when the application is done
            scanner.close();
        }
    }

    /**
     * Validates if the given path is a valid directory.
     *
     * @param directory The directory to validate
     * @return true if the path exists and is a directory, false otherwise
     */
    private boolean isValidDirectory(File directory) {
        return directory != null && directory.exists() && directory.isDirectory();
    }

    /**
     * Gets the source and destination directory paths from user input.
     * This method is separated to make it easier to change the input mechanism in the future.
     *
     * @return An array containing [sourcePath, destinationPath]
     */
    private String[] getDirectoryPaths() {
        // Get source directory path
        System.out.print("Enter source directory path: ");
        String sourcePath = scanner.nextLine();

        // Get destination directory path
        System.out.print("Enter destination directory path: ");
        String destPath = scanner.nextLine();

        return new String[]{sourcePath, destPath};
    }

    /**
     * Displays the comparison results to the user and offers to copy missing files.
     *
     * @param comparisonResult Root node of the hierarchical structure representing differences
     * @param sourceDir The source directory
     * @param destDir The destination directory
     */
    private void displayResults(DirectoryNode comparisonResult, File sourceDir, File destDir) {
        if (!comparisonResult.hasChildren()) {
            System.out.println("\nAll files and directories from source exist in destination.");
        } else {
            System.out.println("\nItems present in source but missing from destination:");

            // Print the differences in a hierarchical format
            printDifferences(comparisonResult);

            // Count total items in the hierarchy
            int totalItems = countItems(comparisonResult) - 1; // -1 to exclude root dir

            System.out.println("\nTotal missing items: " + totalItems);

            // Ask if user wants to copy the files
            promptToCopyFiles(comparisonResult, sourceDir, destDir);
        }
    }

    /**
     * Counts the total number of nodes in the hierarchy
     *
     * @param node The root node to count
     * @return Number of nodes in the hierarchy
     */
    private int countItems(Node node) {
        int count = 1; // Count this node

        if (node.isDirectory()) {
            DirectoryNode dirNode = (DirectoryNode) node;
            if (dirNode.getChildren() != null) {
                for (Node child : dirNode.getChildren().values()) {
                    count += countItems(child);
                }
            }
        }

        return count;
    }

    /**
     * Prints the differences in a hierarchical format to the console.
     *
     * @param node The root node of the differences hierarchy
     */
    private void printDifferences(Node node) {
        printDifferencesInternal(node, 0);
    }

    /**
     * Internal method to recursively print the differences with proper indentation.
     *
     * @param node Current node to print
     * @param level Indentation level
     */
    private void printDifferencesInternal(Node node, int level) {
        // Create indentation based on level
        StringBuilder indent = new StringBuilder();
        for (int i = 0; i < level; i++) {
            indent.append("  ");
        }

        // Print the current node
        System.out.println(indent + "- " + node.getName() + (node.isDirectory() ? "/" : ""));

        // Print children if this is a directory
        if (node.isDirectory()) {
            DirectoryNode dirNode = (DirectoryNode) node;
            for (Node child : dirNode.getChildren().values()) {
                printDifferencesInternal(child, level + 1);
            }
        }
    }

    /**
     * Asks the user if they want to copy missing files from source to destination.
     *
     * @param comparisonResult Root node of the hierarchical structure representing differences
     * @param sourceDir The source directory
     * @param destDir The destination directory
     */
    private void promptToCopyFiles(DirectoryNode comparisonResult, File sourceDir, File destDir) {
        System.out.print("\nDo you want to copy these files from source to destination? (y/n): ");
        String response = scanner.nextLine().trim().toLowerCase();

        if (response.equals("y") || response.equals("yes")) {
            try {
                System.out.println("\nCopying files...");
                int copiedCount = comparator.copyMissingItems();

                // Count total items in the hierarchy for reporting
                int totalItems = countItems(comparisonResult) - 1; // -1 to exclude root dir

                System.out.println("\nOperation complete. Successfully copied " + copiedCount + " out of " + totalItems + " items.");
            } catch (IOException e) {
                System.err.println("\nAn error occurred while copying files: " + e.getMessage());
                System.err.println("Some files may not have been copied.");
            }
        } else {
            System.out.println("\nNo files were copied.");
        }
    }

}
